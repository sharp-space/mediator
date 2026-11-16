using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Sharp.Mediator.SourceGenerator.Analysis;
using Sharp.Mediator.SourceGenerator.Generation;

namespace Sharp.Mediator.SourceGenerator;

/// <summary>
/// Incremental generator that replaces Sharp.Mediator's runtime reflection: it discovers handlers at compile time and emits closed-generic, AOT-safe DI
/// registrations plus the module initializer that contributes them.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MediatorGenerator : IIncrementalGenerator
{
    /// <summary>Tracking name of the source-emission step, so tests can assert it stays cached across unrelated edits.</summary>
    internal const string EmissionTrackingName = "SharpMediatorEmission";

    /// <summary>Tracking name of the referenced-assembly scan, so tests can assert it does not rerun on source edits.</summary>
    internal const string ReferenceScanTrackingName = "SharpMediatorReferenceScan";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) =>
                    node is ClassDeclarationSyntax or RecordDeclarationSyntax
                    && ((TypeDeclarationSyntax)node).BaseList is not null,
                static (syntaxContext, cancellationToken) =>
                    HandlerAnalysis.GetHandlers(syntaxContext.Node, syntaxContext.SemanticModel, cancellationToken))
            .Where(static x => x is not null)
            .Select(static (handlerInfos, _) => handlerInfos!.Value);

        // Open-generic middleware declared in this compilation (closed middleware registers at runtime).
        var middlewares = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) =>
                    node is ClassDeclarationSyntax or RecordDeclarationSyntax
                    && ((TypeDeclarationSyntax)node).BaseList is not null
                    && ((TypeDeclarationSyntax)node).TypeParameterList is not null,
                static (syntaxContext, cancellationToken) =>
                    MiddlewareAnalysis.GetMiddlewares(syntaxContext.Node, syntaxContext.SemanticModel, cancellationToken))
            .Where(static x => x is not null)
            .Select(static (middlewareInfos, _) => middlewareInfos!.Value);

        // Open-generic middleware that referenced assemblies make visible to this one (e.g. the
        // Sharp.Mediator.Validation package), and the messages their handlers dispatch. Every handler assembly closes
        // the middleware it sees over its own messages, and over the referenced messages no other generator saw
        // together with that middleware, so registering the middleware once applies to handlers generated into any
        // assembly; whether a closer actually runs stays gated at runtime on what the user registered. Metadata only
        // changes with the reference set, so the comparer keeps this scan from running on every keystroke.
        var referenceScan = context.CompilationProvider
            .WithComparer(SameReferencesComparer.Instance)
            .Select(static (compilation, cancellationToken) => ReferenceAnalysis.ScanReferencedAssemblies(compilation, cancellationToken))
            .WithTrackingName(ReferenceScanTrackingName);

        var referencedMiddlewares = referenceScan.Select(static (scan, _) => scan.Middlewares);
        var referencedMessages = referenceScan.Select(static (scan, _) => scan.Messages);

        // A middleware whose constraints name a variant type can apply to messages that matching names misses. Those
        // pairs need the compilation, which changes on every edit, so this step reruns while its answer, a sorted list
        // of names, keeps source emission cached.
        var varianceSatisfied = handlers.Collect()
            .Combine(middlewares.Collect())
            .Combine(referenceScan)
            .Combine(context.CompilationProvider)
            .Select(static (data, cancellationToken) =>
            {
                var (((handlerSets, middlewareSets), scan), compilation) = data;
                return Applicability.VarianceSatisfied(
                    [.. handlerSets.SelectMany(static x => x), .. scan.Messages.Select(static x => x.Handler)],
                    Flatten(middlewareSets, scan.Middlewares), compilation, cancellationToken);
            });

        var generatedNamespace = context.CompilationProvider
            .Select(static (compilation, _) => EncodeAssemblyIdentifier(compilation.AssemblyName ?? "UnnamedAssembly"));

        var interceptedCalls = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) =>
                    node is InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "Request" or "RequestStream" or "Publish"
                        }
                    },
                static (syntaxContext, cancellationToken) => GetInterceptedCall(syntaxContext, cancellationToken))
            .Where(static x => x is not null)
            .Select(static (call, _) => call!.Value);

        // Implementation output: every edit moves the locations in its file, and nothing in the IDE needs the result.
        context.RegisterImplementationSourceOutput(interceptedCalls.Collect(), Emitter.EmitInterceptors);

        // The analyzers report the SMD diagnostics, and source emission leaves out what they report as an error. A
        // build that skips analyzers would then compile without those declarations and fail only at the first dispatch,
        // so there the generator reports the same diagnostics, at the same locations and under the same severity
        // settings. An implementation output, because the IDE runs none, and its live analysis runs the analyzers.
        var analyzersSkipped = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) => AnalyzersSkipped(options.GlobalOptions));

        context.RegisterImplementationSourceOutput(
            analyzersSkipped.Combine(context.CompilationProvider),
            static (productionContext, data) =>
            {
                if (data.Left)
                    DiagnosticAnalysis.Analyze(data.Right, productionContext.CancellationToken, productionContext.ReportDiagnostic);
            });

        // Whether generated code can bind to the runtime: false for the core library itself and for compilations that
        // see the analyzer without referencing Sharp.Mediator.
        var consumesRuntime = context.CompilationProvider
            .Select(static (compilation, _) => ConsumesRuntime(compilation));

        // The generator only ever runs on C#, so its parse options are always C#'s.
        var languageVersion = context.ParseOptionsProvider
            .Select(static (options, _) => ((CSharpParseOptions)options).LanguageVersion);

        var combined = handlers.Select(static (handlerInfos, _) => WithoutLocations(handlerInfos)).Collect()
            .Combine(middlewares.Select(static (middlewareInfos, _) => WithoutLocations(middlewareInfos)).Collect())
            .Combine(referencedMiddlewares)
            .Combine(referencedMessages)
            .Combine(generatedNamespace)
            .Combine(consumesRuntime)
            .Combine(varianceSatisfied)
            .Combine(languageVersion);

        context.RegisterSourceOutput(
            combined.WithTrackingName(EmissionTrackingName),
            static (productionContext, data) =>
            {
                var (((((((handlerSets, middlewareSets), referenced), messages), assemblyIdentifier), runtime), variance), version) = data;
                Emitter.Emit(
                    productionContext, Flatten(handlerSets), Flatten(middlewareSets, referenced), messages.Values,
                    assemblyIdentifier, runtime, variance, version);
            });
    }

    // The SDK's rule for skipping analyzers, from _ComputeSkipAnalyzers in Microsoft.Managed.Core.targets, and it has to
    // follow that target when the SDK changes it. RunAnalyzers decides when it is set, RunAnalyzersDuringBuild otherwise.
    // Where neither skips them, a build Visual Studio starts to debug or run tests does, unless warnings are errors or
    // OptimizeImplicitlyTriggeredBuild decides. The properties reach the generator through Sharp.Mediator.targets, a
    // property that is not set arrives empty, and MSBuild compares them ignoring case.
    private static bool AnalyzersSkipped(AnalyzerConfigOptions options)
    {
        var runAnalyzers = Property(options, "RunAnalyzers");
        if (IsFalse(runAnalyzers) || (runAnalyzers.Length == 0 && IsFalse(Property(options, "RunAnalyzersDuringBuild"))))
            return true;

        var optimize = Property(options, "OptimizeImplicitlyTriggeredBuild");
        return IsTrue(Property(options, "IsImplicitlyTriggeredBuild")) &&
            IsTrue(Property(options, "UsingMicrosoftNETSdk")) &&
            !IsFalse(optimize) &&
            (!IsTrue(Property(options, "TreatWarningsAsErrors")) || IsTrue(optimize));
    }

    private static string Property(AnalyzerConfigOptions options, string name) =>
        options.TryGetValue("build_property." + name, out var value) ? value : "";

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string value) => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    private static bool ConsumesRuntime(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Sharp.Mediator.Bindings.HandlerRegistry") is { } registry &&
        !SymbolEqualityComparer.Default.Equals(registry.ContainingAssembly, compilation.Assembly);

    private static ImmutableArray<HandlerInfo> Flatten(ImmutableArray<EquatableArray<HandlerInfo>> handlerSets) =>
        [.. handlerSets.SelectMany(static x => x)];

    private static ImmutableArray<MiddlewareInfo> Flatten(
        ImmutableArray<EquatableArray<MiddlewareInfo>> middlewareSets,
        EquatableArray<MiddlewareInfo> referenced
    ) => [.. middlewareSets.SelectMany(static x => x), .. referenced];

    private static EquatableArray<HandlerInfo> WithoutLocations(EquatableArray<HandlerInfo> handlers) =>
        new([.. handlers.Select(static x => x with { HandlerLocation = LocationSnapshot.None })]);

    private static EquatableArray<MiddlewareInfo> WithoutLocations(EquatableArray<MiddlewareInfo> middlewares) =>
        new([.. middlewares.Select(static x => x with { Location = LocationSnapshot.None })]);

    // Opt-in through the compiler's own switch: an interceptor outside <InterceptorsNamespaces> is a compile error. The
    // emitted class is file-local, which needs C# 11.
    private static bool InterceptorsEnabled(ParseOptions options) =>
        options is CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp11 } &&
        options.Features.TryGetValue("InterceptorsNamespaces", out var namespaces) &&
        namespaces.Contains(Emitter.InterceptorsNamespace);

    private static InterceptedCall? GetInterceptedCall(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        if (!InterceptorsEnabled(context.Node.SyntaxTree.Options)) return null;

        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
            !(Discovery.IsMediatorType(method.ContainingType, "IRequester") || Discovery.IsMediatorType(method.ContainingType, "IPublisher")))
            return null;

        // The interceptor receives the receiver as IRequester or IPublisher. A value type would be boxed, so a
        // call that changes its state would change a copy, and a type parameter that allows ref structs does not
        // convert at all.
        var receiver = ((MemberAccessExpressionSyntax)invocation.Expression).Expression;
        if (!context.SemanticModel.GetTypeInfo(receiver, cancellationToken).Type!.IsReferenceType) return null;

        InterceptedKind kind;
        ITypeSymbol? response = null;
        switch (method.Name, method.TypeArguments.Length, method.Parameters.Length)
        {
            case ("Request", 2, _):
                kind = InterceptedKind.Request;
                response = method.TypeArguments[1];
                break;
            case ("Request", 1, _) when method.OriginalDefinition.Parameters[0].Type is ITypeParameterSymbol:
                kind = InterceptedKind.VoidRequest;
                break;
            case ("Request", 1, _):
                kind = InterceptedKind.InferredRequest;
                response = method.TypeArguments[0];
                break;
            case ("RequestStream", 2, _):
                kind = InterceptedKind.Stream;
                response = method.TypeArguments[1];
                break;
            case ("RequestStream", 1, _):
                kind = InterceptedKind.InferredStream;
                response = method.TypeArguments[0];
                break;
            case ("Publish", 1, 2):
                kind = InterceptedKind.Event;
                break;
            default:
                return null;
        }

        // An inferred call names no message type, so the argument's static type picks the instantiation. The runtime
        // type is still checked before that instantiation is used, unless nothing can derive from it, which is why an
        // open class qualifies.
        var message = method.TypeArguments[0];
        if (kind is InterceptedKind.InferredRequest or InterceptedKind.InferredStream)
        {
            var argument = invocation.ArgumentList.Arguments[0];
            if (argument.NameColon is not null ||
                context.SemanticModel.GetTypeInfo(argument.Expression, cancellationToken).Type
                    is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } argumentType)
                return null;

            message = argumentType;
        }

        // A call of a type the compilation cannot find is left for the compiler to report. A call whose types generated
        // code cannot write, such as one from generic code, gets a generic interceptor that names none of them.
        if (message.TypeKind == TypeKind.Error || response is { TypeKind: TypeKind.Error }) return null;

        var compilation = context.SemanticModel.Compilation;
        var unnamed = !Discovery.NameableFromGeneratedCode(message, compilation) ||
            (response is not null && !Discovery.NameableFromGeneratedCode(response, compilation));

        // The compiler intercepts a call inside an expression tree too, and the tree then records the generated method,
        // so a library that reads it, such as a Moq setup, no longer finds the mediator call.
        if (InExpressionTree(context.SemanticModel, invocation, cancellationToken)) return null;

        // Never null here: the predicate only lets through a call whose target is a named member.
        var location = context.SemanticModel.GetInterceptableLocation(invocation, cancellationToken)!;
        if (unnamed) return new InterceptedCall(location.Version, location.Data, kind, "", null, InterceptedLookup.Unnamed);

        // Nothing derives from a value type, nor from a sealed class compiled here, since unsealing it compiles this
        // call again. A class of another assembly can be unsealed without that, and a derived type given a handler of
        // its own, so whether it is sealed is read when the program runs.
        var lookup = message switch
        {
            { IsValueType: true } => InterceptedLookup.Exact,
            { TypeKind: TypeKind.Class, IsAbstract: false }
                when !SymbolEqualityComparer.Default.Equals(message.ContainingAssembly, compilation.Assembly) =>
                InterceptedLookup.ExactIfSealed,
            { TypeKind: TypeKind.Class, IsSealed: true } => InterceptedLookup.Exact,
            _ => InterceptedLookup.Checked
        };

        return new InterceptedCall(
            location.Version, location.Data, kind,
            message.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            response?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            lookup);
    }

    // Every enclosing lambda counts, not only the nearest: inside a tree, a nested lambda converts to a delegate and
    // only the outermost to the tree. A query clause over IQueryable is a lambda of its own in the operation tree. The
    // target can be LambdaExpression or Expression as well as Expression<T>, hence the walk over its base types.
    private static bool InExpressionTree(SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
    {
        if (!invocation.Ancestors().Any(static x => x is AnonymousFunctionExpressionSyntax or QueryExpressionSyntax)) return false;

        var expression = model.Compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression");
        for (var operation = model.GetOperation(invocation, cancellationToken)!.Parent; operation is not null; operation = operation.Parent)
        {
            if (operation is not IAnonymousFunctionOperation) continue;

            for (var type = operation.Parent!.Type; type is not null; type = type.BaseType)
                if (SymbolEqualityComparer.Default.Equals(type, expression))
                    return true;
        }

        return false;
    }

    internal static string EncodeAssemblyIdentifier(string assemblyName)
    {
        var builder = new StringBuilder(assemblyName.Length + 1);
        builder.Append('A');
        foreach (var character in assemblyName)
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(character);
            else builder.Append('_').Append(((int)character).ToString("X4"));

        return builder.ToString();
    }

    /// <summary>Treats compilations as equal while their assembly identity and reference set are unchanged.</summary>
    internal sealed class SameReferencesComparer : IEqualityComparer<Compilation>
    {
        public static readonly SameReferencesComparer Instance = new();

        public bool Equals(Compilation? left, Compilation? right) =>
            left!.AssemblyName == right!.AssemblyName && left.ExternalReferences.SequenceEqual(right.ExternalReferences);

        public int GetHashCode(Compilation compilation) => compilation.ExternalReferences.Length;
    }
}