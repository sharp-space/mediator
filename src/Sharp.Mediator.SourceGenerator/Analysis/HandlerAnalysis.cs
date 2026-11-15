using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sharp.Mediator.SourceGenerator.Generation;
using static Sharp.Mediator.SourceGenerator.DiagnosticDescriptors;
using static Sharp.Mediator.SourceGenerator.Analysis.SymbolNames;

namespace Sharp.Mediator.SourceGenerator.Analysis;

internal static class HandlerAnalysis
{
    public static void ReportDiagnostics(
        Action<Diagnostic> report,
        ImmutableArray<HandlerInfo> handlers,
        ImmutableArray<MiddlewareInfo> middlewares,
        ImmutableArray<UnreachableMiddlewareInfo> unreachableMiddlewares,
        ImmutableArray<GenericHandlerInfo> genericHandlers,
        Compilation compilation
    )
    {
        void Report(DiagnosticDescriptor descriptor, LocationSnapshot location, params object?[] arguments) =>
            report(Diagnostic.Create(descriptor, location.ToLocation(compilation), arguments));

        Analyze(Report, handlers, middlewares);

        foreach (var handler in genericHandlers
                     .Distinct()
                     .OrderBy(static x => x.HandlerFullyQualifiedName, StringComparer.Ordinal))
            Report(GenericHandler, handler.Location, SimpleName(handler.HandlerFullyQualifiedName));

        foreach (var handler in handlers
                     .Where(static x => x is { HandlerAccessible: false, MessageAccessible: true })
                     .Select(static x => (x.HandlerFullyQualifiedName, x.HandlerLocation))
                     .Distinct()
                     .OrderBy(static x => x.HandlerFullyQualifiedName, StringComparer.Ordinal))
            Report(InaccessibleHandler, handler.HandlerLocation, SimpleName(handler.HandlerFullyQualifiedName));

        foreach (var middleware in unreachableMiddlewares
                     .Distinct()
                     .OrderBy(static x => x.MiddlewareFullyQualifiedName + x.ServiceInterfaceFullyQualifiedName, StringComparer.Ordinal))
            Report(
                UnreachableMiddleware, middleware.Location,
                SimpleName(middleware.MiddlewareFullyQualifiedName), DisplayName(middleware.ServiceInterfaceFullyQualifiedName),
                SimpleName(middleware.MessageFullyQualifiedName));
    }

    internal static (List<MiddlewareInfo> Middlewares, HashSet<string> ExcludedMessages) Analyze(
        DiagnosticSink report,
        ImmutableArray<HandlerInfo> handlers,
        ImmutableArray<MiddlewareInfo> middlewares
    )
    {
        var closeableMiddlewares = MiddlewareAnalysis.PrepareMiddlewares(report, middlewares);
        var excludedMessages = MessageAnalysis.ReportInvalidMessageShapes(report, handlers);
        MessageAnalysis.ReportInterfaceRequestHandlers(report, handlers, excludedMessages);
        ReportMultipleHandlers(report, handlers, excludedMessages);
        excludedMessages.UnionWith(MessageAnalysis.ReportInaccessibleMessages(report, handlers));
        return (closeableMiddlewares, excludedMessages);
    }

    private static void ReportMultipleHandlers(
        DiagnosticSink report,
        ImmutableArray<HandlerInfo> handlers,
        HashSet<string> invalid
    )
    {
        foreach (var group in handlers
                     .Where(static x => x is { HandlerAccessible: true, Kind: MessageKind.VoidRequest or MessageKind.Request or MessageKind.Stream })
                     .GroupBy(static x => x.ServiceInterfaceFullyQualifiedName)
                     .OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            var implementations = group
                .Select(static x => x.HandlerFullyQualifiedName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static x => x, StringComparer.Ordinal)
                .ToArray();

            if (implementations.Length < 2) continue;

            var first = group.First();
            invalid.Add(first.MessageFullyQualifiedName);
            report(
                MultipleHandlers, first.HandlerLocation,
                DisplayName(first.ServiceInterfaceFullyQualifiedName), string.Join(", ", implementations.Select(DisplayName)));
        }
    }

    internal static EquatableArray<HandlerInfo>? GetHandlers(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var declaration = (TypeDeclarationSyntax)node;
        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken)!;

        // Only concrete, non-generic classes can be registered handlers.
        if (symbol.TypeKind != TypeKind.Class || symbol.IsAbstract || symbol.IsGenericType)
            return null;

        var handlers = Discovery.GetHandlers(
            symbol, semanticModel.Compilation.Assembly).ToImmutableArray();

        return handlers.Length > 0 ? new EquatableArray<HandlerInfo>(handlers) : null;
    }

    internal static GenericHandlerInfo? GetGenericHandler(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var declaration = (TypeDeclarationSyntax)node;
        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken)!;
        if (symbol.TypeKind != TypeKind.Class || symbol.IsAbstract || !symbol.IsGenericType ||
            !Discovery.ImplementsHandlerInterface(symbol))
            return null;

        return new GenericHandlerInfo(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            LocationSnapshot.From(symbol.Locations.First(static x => x.IsInSource)));
    }

    /// <summary>Receives one diagnostic; source emission passes a no-op because it only needs the exclusions.</summary>
    internal delegate void DiagnosticSink(DiagnosticDescriptor descriptor, LocationSnapshot location, params object?[] arguments);
}