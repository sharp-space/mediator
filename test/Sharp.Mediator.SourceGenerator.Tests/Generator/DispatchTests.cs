using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using static Sharp.Mediator.SourceGenerator.Tests.Fixtures.GeneratorTestHarness;

namespace Sharp.Mediator.SourceGenerator.Tests.Generator;

// Generated registrar source-shape contracts.
public sealed class DispatchTests
{
    // ---- generated-source shapes ------------------------------------------------------------------------------------

    [Fact]
    public void Registrar_ContributesOneRouteFinderPerKind()
    {
        const string source = """
                              using System.Collections.Generic;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed class Ping : IRequest<int>;
                              public sealed class Noop : IRequest;
                              public sealed class Signal : IEvent;
                              public sealed class Count : IStreamRequest<int>;

                              public sealed class PingHandler : IRequestHandler<Ping, int>
                              {
                                  public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => new(1);
                              }

                              public sealed class NoopHandler : IRequestHandler<Noop>
                              {
                                  public ValueTask Handle(Noop message, CancellationToken cancellationToken) => default;
                              }

                              public sealed class SignalHandler : IEventHandler<Signal>
                              {
                                  public ValueTask Handle(Signal message, CancellationToken cancellationToken) => default;
                              }

                              public sealed class CountHandler : IStreamRequestHandler<Count, int>
                              {
                                  public async IAsyncEnumerable<int> Handle(Count message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                                  {
                                      yield return 1;
                                  }
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var text = RegistrationsText(run);
        Assert.Contains(
            "MediatorRuntime.AddAssembly(1, typeof(GeneratedMediatorRegistrar).Assembly, FindRequestRoute, FindEventRoute, null, FindStreamRoute, HandlerServices, null)",
            text);

        Assert.DoesNotContain("MatchEventInterfaces", text);
        Assert.Contains("typeof(global::Sharp.Mediator.IRequestHandler<global::GeneratorFixture.Ping, int>),", text);
        Assert.Contains("typeof(global::Sharp.Mediator.IRequestHandler<global::GeneratorFixture.Noop>),", text);
        Assert.Contains("typeof(global::Sharp.Mediator.IEventHandler<global::GeneratorFixture.Signal>),", text);
        Assert.Contains("typeof(global::Sharp.Mediator.IStreamRequestHandler<global::GeneratorFixture.Count, int>),", text);
        Assert.DoesNotContain("Slot", text);

        Assert.Contains(
            "if (messageType == typeof(global::GeneratorFixture.Ping)) return global::Sharp.Mediator.Bindings.MediatorFactory.CreateRequestRoute<global::GeneratorFixture.Ping, int>();",
            text);

        Assert.Contains(
            "if (messageType == typeof(global::GeneratorFixture.Noop)) return global::Sharp.Mediator.Bindings.MediatorFactory.CreateVoidRequestRoute<global::GeneratorFixture.Noop>();",
            text);

        Assert.Contains(
            "if (messageType == typeof(global::GeneratorFixture.Signal)) return global::Sharp.Mediator.Bindings.MediatorFactory.CreateEventRoute<global::GeneratorFixture.Signal>();",
            text);

        Assert.Contains(
            "if (messageType == typeof(global::GeneratorFixture.Count)) return global::Sharp.Mediator.Bindings.MediatorFactory.CreateStreamRoute<global::GeneratorFixture.Count, int>();",
            text);
    }

    [Fact]
    // An event reaches handlers declared for the interfaces it is. The runtime cannot list those reliably once trimmed,
    // so each assembly reports the interfaces it declares event handlers for with an `is` check of its own.
    public void InterfaceEventHandler_EmitsAnInterfaceMatcher()
    {
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public interface IAudited : IEvent;
                              public interface ITracked : IEvent;
                              public sealed record Signal : IAudited, ITracked;

                              public sealed class SignalHandler : IEventHandler<Signal>
                              {
                                  public ValueTask Handle(Signal message, CancellationToken cancellationToken) => default;
                              }

                              public sealed class AuditedHandler : IEventHandler<IAudited>
                              {
                                  public ValueTask Handle(IAudited message, CancellationToken cancellationToken) => default;
                              }

                              public sealed class TrackedHandler : IEventHandler<ITracked>
                              {
                                  public ValueTask Handle(ITracked message, CancellationToken cancellationToken) => default;
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var text = RegistrationsText(run);
        Assert.Contains(
            "MediatorRuntime.AddAssembly(1, typeof(GeneratedMediatorRegistrar).Assembly, FindRequestRoute, FindEventRoute, MatchEventInterfaces, FindStreamRoute, HandlerServices, null)",
            text);

        Assert.Contains("if (message is global::GeneratorFixture.IAudited) types.Add(typeof(global::GeneratorFixture.IAudited));", text);
        Assert.Contains("if (message is global::GeneratorFixture.ITracked) types.Add(typeof(global::GeneratorFixture.ITracked));", text);
        Assert.DoesNotContain("message is global::GeneratorFixture.Signal", text);

        Assert.Contains(
            "if (messageType == typeof(global::GeneratorFixture.IAudited)) return global::Sharp.Mediator.Bindings.MediatorFactory.CreateEventRoute<global::GeneratorFixture.IAudited>();",
            text);
    }

    [Fact]
    public void GeneratedSize_DoesNotGrowADispatchMethodPerMessage()
    {
        // Regression: per-message `typeof` chains made dispatch linear in the message count, and past a few hundred
        // messages the method grew too large for the JIT to optimize at all. Registration is the only generated code
        // now, and it runs once at startup.
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed class Ping : IRequest<int>;

                              public sealed class PingHandler : IRequestHandler<Ping, int>
                              {
                                  public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        var generated = Assert.Single(Assert.Single(run.Result.Results).GeneratedSources);
        Assert.Equal("SharpMediator.Registrations.g.cs", generated.HintName);
        Assert.DoesNotContain("IMediator)", generated.SourceText.ToString());
    }

    // ---- handler registrar marker -----------------------------------------------------------------------------------

    [Fact]
    public void CompilationWithoutHandlers_EmitsOnlyTheHandlerRegistrarMarker()
    {
        var run = RunGenerator("namespace GeneratorFixture; public sealed record OnlyAContract : Sharp.Mediator.IRequest<int>;");
        AssertCompiles(run);
        AssertOnlyHandlerRegistrarMarker(run);
    }

    [Fact]
    public void AddHandlers_GeneratorAppliedAssemblyWithoutHandlers_IsANoOp()
    {
        // An assembly with nothing to register is not one missing the generator. This project declares no handlers of
        // its own, so the host gives AddMediator an assembly to dispatch to.
        var host = CompileGeneratedToAssembly("MarkerHost", """
                                                            using System.Threading;
                                                            using System.Threading.Tasks;
                                                            using Sharp.Mediator;
                                                            namespace GeneratorFixture;

                                                            public sealed class Ping : IRequest<int>;

                                                            public sealed class PingHandler : IRequestHandler<Ping, int>
                                                            {
                                                                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                                            }
                                                            """);

        var handlerless = CompileGeneratedToAssembly(
            "MarkerOnly", "namespace GeneratorFixture; public sealed record OnlyAContract : Sharp.Mediator.IRequest<int>;");

        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandlers(host).AddHandlers(handlerless));
        Assert.DoesNotContain(services, x => x.ImplementationType?.Assembly == handlerless);
    }

    [Fact]
    public void CoreLibraryItself_EmitsNothing()
    {
        // The generator is applied to Sharp.Mediator so its DLL exists for packaging; a registrar there would be an
        // initializer calling into its own infrastructure.
        var run = RunGenerator("""
                               namespace Sharp.Mediator.Bindings;
                               public static class HandlerRegistry;
                               """);

        Assert.Empty(Assert.Single(run.Result.Results).GeneratedSources);
    }

    [Fact]
    public void CompilationNotReferencingTheRuntime_EmitsNothing()
    {
        // Solutions can apply the analyzer broadly, as this repository does for test projects that only use Responses.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "NoRuntimeReference",
            [CSharpSyntaxTree.ParseText("public sealed class Plain;", parseOptions, "Plain.cs")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = CSharpGeneratorDriver
            .Create([new MediatorGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGenerators(compilation)
            .GetRunResult();

        Assert.Empty(Assert.Single(result.Results).GeneratedSources);
    }
}