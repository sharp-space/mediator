using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Sharp.Mediator.SourceGenerator.Generation;

namespace Sharp.Mediator.SourceGenerator.Analysis;

internal static class ReferenceAnalysis
{
    internal static ReferenceScan ScanReferencedAssemblies(Compilation compilation, CancellationToken cancellationToken)
    {
        var scan = new ReferenceScanBuilder(compilation);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Cheap identity-level prefilter: only assemblies that reference Sharp.Mediator can declare
            // middleware or handlers, which skips the framework and most packages without realizing any of their
            // type symbols. Generated code names types with global::, which cannot reach an assembly referenced
            // only through an extern alias.
            if (!ReferencesMediator(assembly) || Discovery.RequiresExternAlias(assembly, compilation)) continue;

            ScanNamespace(assembly.GlobalNamespace, assembly, scan, cancellationToken);
        }

        return scan.Build();
    }

    private static bool ReferencesMediator(IAssemblySymbol assembly) =>
        assembly.Modules.SelectMany(static x => x.ReferencedAssemblies).Any(static x => x.Name == "Sharp.Mediator");

    private static void ScanNamespace(
        INamespaceSymbol namespaceSymbol,
        IAssemblySymbol assembly,
        ReferenceScanBuilder scan,
        CancellationToken cancellationToken
    )
    {
        foreach (var member in namespaceSymbol.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamespaceSymbol nested) ScanNamespace(nested, assembly, scan, cancellationToken);
            else ScanType((INamedTypeSymbol)member, assembly, scan);
        }
    }

    // Nested types are visited because a middleware or handler nested in a public class is as reachable as
    // a top-level one.
    private static void ScanType(INamedTypeSymbol type, IAssemblySymbol assembly, ReferenceScanBuilder scan)
    {
        // ReSharper disable once MergeIntoPattern — a property pattern adds a null test, which Coverlet reports
        // as a branch of its own, and the symbol is never null, so no test can cover that branch and the 100%
        // branch gate fails.
        if (type.TypeKind == TypeKind.Class && !type.IsAbstract)
        {
            if (type.Arity > 0) scan.AddMiddlewares(type);
            else if (!type.IsGenericType) scan.AddHandlers(type, assembly);
        }

        foreach (var nested in type.GetTypeMembers())
            ScanType(nested, assembly, scan);
    }

    /// <summary>Collects one scan of the referenced assemblies, keeping each handler's assembly until the pairs are settled.</summary>
    private sealed class ReferenceScanBuilder(Compilation compilation)
    {
        private readonly INamedTypeSymbol? _assemblyMetadata =
            compilation.GetTypeByMetadataName("System.Reflection.AssemblyMetadataAttribute");

        private readonly Dictionary<IAssemblySymbol, HashSet<string>> _closedMiddleware = new(SymbolEqualityComparer.Default);
        private readonly List<(HandlerInfo Info, IAssemblySymbol Assembly)> _handlers = [];
        private readonly List<MiddlewareInfo> _middlewares = [];

        // Only what this assembly can close goes in: a middleware it cannot name, or of a shape no closer handles, is
        // the declaring assembly's own business, so nothing is reported about it here.
        public void AddMiddlewares(INamedTypeSymbol type)
        {
            foreach (var middleware in Discovery.GetMiddlewares(type, compilation.Assembly))
                if (middleware.Issues == MiddlewareIssue.None)
                    _middlewares.Add(middleware);
        }

        // A message generated code cannot name stays a gap for the runtime to report, and one its own assembly's
        // generator rejects is never dispatched, so neither is closed over.
        public void AddHandlers(INamedTypeSymbol type, IAssemblySymbol assembly)
        {
            var nameable = new HashSet<string>(
                type.AllInterfaces
                    .Where(x => x.TypeArguments.All(argument => Discovery.NameableFromGeneratedCode(argument, compilation)))
                    .Select(static x => x.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                StringComparer.Ordinal);

            foreach (var handler in Discovery.GetHandlers(type, compilation.Assembly))
                if (nameable.Contains(handler.ServiceInterfaceFullyQualifiedName) && IsDispatched(handler))
                    _handlers.Add((handler, assembly));
        }

        private static bool IsDispatched(HandlerInfo handler) =>
            handler is { MessageImplementsBothRequestInterfaces: false, MessageImplementsMultipleResponseInterfaces: false } &&
            !(handler.MessageIsInterface && handler.Kind != MessageKind.Event);

        public ReferenceScan Build()
        {
            var messages = _handlers
                .GroupBy(static x => (x.Info.Kind, x.Info.MessageFullyQualifiedName))
                .OrderBy(static x => x.Key.MessageFullyQualifiedName, StringComparer.Ordinal)
                .ThenBy(static x => x.Key.Kind)
                .Select(group => new ClosingTarget(
                    group.First().Info,
                    new EquatableArray<string>([
                        .. _middlewares
                            .Where(middleware => group.Any(handler => ClosedByHandlerAssembly(handler.Assembly, middleware)))
                            .Select(static x => x.MatchTypeFullyQualifiedName)
                            .Distinct()
                            .OrderBy(static x => x, StringComparer.Ordinal)
                    ])));

            return new ReferenceScan(
                new EquatableArray<MiddlewareInfo>([.. _middlewares]),
                new EquatableArray<ClosingTarget>([.. messages]));
        }

        // A handler assembly's generated code records each middleware its closer closes over the assembly's messages:
        // its own, and those of the assemblies it referenced when it was built. A middleware it does not record is
        // closed here, as is one added to its package after the handler assembly was built, which a reference to the
        // package by name used to pass for closed. A middleware assembly that references the handler assembly may have
        // closed the pair too, but it may have been built without the generator, as packages are, so that pair is
        // closed here as well.
        private bool ClosedByHandlerAssembly(IAssemblySymbol handlerAssembly, MiddlewareInfo middleware) =>
            ClosedMiddleware(handlerAssembly)
                .Contains(Emitter.ClosedMiddlewareRecord(middleware.AssemblyName, middleware.MatchTypeFullyQualifiedName));

        // The middleware an assembly's generated code records closing, listed in the value of its assembly metadata
        // under the key Emitter.ClosedMiddlewareKey. Matched by the attribute's symbol and the key, so another
        // attribute of the same name, or metadata under another key, is no record. Nor is a null value, or an attribute
        // whose arguments bind to no constructor, as in a referenced project that does not compile, which then has no
        // arguments at all.
        private HashSet<string> ClosedMiddleware(IAssemblySymbol assembly)
        {
            if (_closedMiddleware.TryGetValue(assembly, out var closed)) return closed;

            closed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in assembly.GetAttributes())
            {
                var arguments = attribute.ConstructorArguments;
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _assemblyMetadata) &&
                    // ReSharper disable once MergeIntoPattern — a list pattern needs System.Index,
                    // which netstandard2.0 lacks.
                    arguments.Length == 2 && Equals(arguments[0].Value, Emitter.ClosedMiddlewareKey) &&
                    arguments[1].Value is string records)
                    closed.UnionWith(records.Split(Emitter.ClosedMiddlewareSeparator));
            }

            _closedMiddleware.Add(assembly, closed);
            return closed;
        }
    }
}