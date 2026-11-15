using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Sharp.Mediator.SourceGenerator.Analysis;
using static Sharp.Mediator.SourceGenerator.DiagnosticDescriptors;

namespace Sharp.Mediator.SourceGenerator;

/// <summary>Reports diagnostics for handler, message and middleware declarations independently of source generation.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MediatorAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        MultipleRequestResponses, BothRequestInterfaces, InaccessibleMessage, MultipleStreamResponses,
        MultipleHandlers, GenericHandler, InaccessibleHandler, InterfaceRequestHandler,
        MiddlewareNotStaticallyCloseable, UnsupportedOpenGenericMiddleware, UnreachableMiddleware
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        // One analysis of the compilation finds every diagnostic listed above. Three analyzers that each reported a
        // share of them ran the whole analysis three times.
        context.RegisterCompilationAction(static analysisContext =>
            DiagnosticAnalysis.Analyze(
                analysisContext.Compilation, analysisContext.CancellationToken, analysisContext.ReportDiagnostic));
    }
}

internal static class DiagnosticDescriptors
{
    internal static readonly DiagnosticDescriptor MultipleRequestResponses = new(
        "SMD001",
        "Request type implements multiple response types",
        "Request type '{0}' implements multiple IRequest<TResponse> response types. A request type must have exactly one response type.",
        "Sharp.Mediator",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor BothRequestInterfaces = new(
        "SMD002",
        "Request type implements both IRequest and IRequest<TResponse>",
        "Request type '{0}' implements both IRequest and IRequest<TResponse>. A request type must implement exactly one of them.",
        "Sharp.Mediator",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor InaccessibleMessage = new(
        "SMD003",
        "Message type is not accessible from generated code",
        "Message type '{0}' (or its response type) is private, protected, or file-local, or nested in such a type, so generated code cannot reference it. Handler '{1}' is excluded from generated dispatch and registration; make the message, and any type it is nested in, internal or public.",
        "Sharp.Mediator",
        DiagnosticSeverity.Warning,
        true);

    internal static readonly DiagnosticDescriptor MultipleStreamResponses = new(
        "SMD004",
        "Stream request type implements multiple response types",
        "Stream request type '{0}' implements multiple IStreamRequest<TResponse> response types. A stream request type must have exactly one response type.",
        "Sharp.Mediator",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor InterfaceRequestHandler = new(
        "SMD008",
        "Request or stream handler is declared for an interface",
        "Handler '{0}' handles interface '{1}', but a request is dispatched to the handler of its runtime type or of one of its base classes, never of an interface, so this handler can never run. Declare it for a class or struct instead.",
        "Sharp.Mediator",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor MiddlewareNotStaticallyCloseable = new(
        "SMD010",
        "Open-generic middleware cannot be closed at build time",
        "Open-generic middleware '{0}' is private, protected, or file-local, or nested in such a type, so generated code cannot reference it. It is closed via reflection at startup under the JIT and fails at startup under NativeAOT; make it, and any type it is nested in, internal or public.",
        "Sharp.Mediator",
        DiagnosticSeverity.Warning,
        true);

    internal static readonly DiagnosticDescriptor UnsupportedOpenGenericMiddleware = new(
        "SMD011",
        "Open-generic middleware shape is not supported",
        "Open-generic middleware '{0}' must declare exactly the middleware interface's generic parameters in the same order and cannot be nested in a generic containing type. Register the required closed middleware types explicitly instead.",
        "Sharp.Mediator",
        DiagnosticSeverity.Warning,
        true);

    internal static readonly DiagnosticDescriptor GenericHandler = new(
        "SMD006",
        "Generic handler is not discovered",
        "Handler '{0}' is generic, so neither generated code nor AddHandlers can register it. Make it abstract and derive a non-generic handler for each message, for example 'sealed class IntHandler : {0}<int>;'.",
        "Sharp.Mediator",
        DiagnosticSeverity.Warning,
        true);

    internal static readonly DiagnosticDescriptor InaccessibleHandler = new(
        "SMD007",
        "Handler is not accessible from generated code",
        "Handler '{0}' is private, protected, or file-local, or nested in such a type, so AddHandlers cannot register it. Make it, and any type it is nested in, internal or public, or register it explicitly with AddHandler.",
        "Sharp.Mediator",
        DiagnosticSeverity.Warning,
        true);

    internal static readonly DiagnosticDescriptor UnreachableMiddleware = new(
        "SMD012",
        "Closed middleware can never run",
        "Middleware '{0}' implements '{1}', but '{2}' does not dispatch through that pipeline, so it never runs. Void requests and events use IMiddleware<TMessage>, requests with a response use IMiddleware<TMessage, TResponse>, and stream requests use IStreamMiddleware<TRequest, TResponse>.",
        "Sharp.Mediator",
        DiagnosticSeverity.Warning,
        true);

    internal static readonly DiagnosticDescriptor MultipleHandlers = new(
        "SMD005",
        "Request or stream has multiple handlers",
        "Handler service '{0}' has multiple implementations: {1}. Requests and streams require exactly one handler.",
        "Sharp.Mediator",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor LanguageVersionTooOld = new(
        "SMD020",
        "Language version is too old for the generated code",
        "Sharp.Mediator generates code that needs C# 9 or later, and this project compiles C# {0}, so nothing was generated for it. Set LangVersion to 9 or later.",
        "Sharp.Mediator",
        DiagnosticSeverity.Error,
        true);
}