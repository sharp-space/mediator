using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static Sharp.Mediator.SourceGenerator.Tests.Fixtures.GeneratorTestHarness;

namespace Sharp.Mediator.SourceGenerator.Tests.Generator;

// Which call sites get an interceptor. An interceptor the compiler rejects breaks a build that was valid without it, so
// everything here compiles the output, and every shape that cannot be intercepted has to come out untouched.
public sealed class InterceptorTests
{
    private const string Interceptors = "SharpMediator.Interceptors.g.cs";

    private const string Messages =
        """
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Sharp.Mediator;
        namespace GeneratorFixture;

        public sealed record Ping : IRequest<int>;
        public record Open : IRequest<int>;
        public abstract record Shape : IRequest<int>;
        public sealed record Square : Shape;
        public readonly record struct Value : IRequest<int>;
        public sealed record Noop : IRequest;
        public sealed record Signal : IEvent;
        public sealed record Count : IStreamRequest<int>;
        public sealed record Named : IRequest<string?>;

        public sealed class PingHandler : IRequestHandler<Ping, int>
        {
            public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => new(1);
        }

        public sealed class OpenHandler : IRequestHandler<Open, int>
        {
            public ValueTask<int> Handle(Open message, CancellationToken cancellationToken) => new(1);
        }

        public sealed class ShapeHandler : IRequestHandler<Shape, int>
        {
            public ValueTask<int> Handle(Shape message, CancellationToken cancellationToken) => new(1);
        }

        public sealed class ValueHandler : IRequestHandler<Value, int>
        {
            public ValueTask<int> Handle(Value message, CancellationToken cancellationToken) => new(1);
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
                await Task.CompletedTask;
            }
        }

        public sealed class NamedHandler : IRequestHandler<Named, string?>
        {
            public ValueTask<string?> Handle(Named message, CancellationToken cancellationToken) => new((string?) null);
        }

        """;

    // A class of another assembly is exact while it is sealed, which its interceptors read where dynamic code is
    // supported, and never where it is not, as under Native AOT.
    private const string DynamicCode = "global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported";

    private static readonly CSharpParseOptions Enabled = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new("InterceptorsNamespaces", "Other.Namespace;Sharp.Mediator.Generated.Interceptors")]);

    private static GeneratorRun Run(
        string calls,
        CSharpParseOptions? options = null,
        MetadataReference[]? references = null,
        string extraTypes = ""
    ) => RunGenerator(
        Messages + extraTypes + "\npublic static class Calls\n{\n" + calls + "\n}\n",
        references, parseOptions: options ?? Enabled);

    // Lines end in \n here whatever the platform wrote, so expectations can span them.
    private static string? InterceptorsOf(GeneratorRun run) =>
        Assert.Single(run.Result.Results).GeneratedSources
            .Where(static x => x.HintName == Interceptors)
            .Select(static x => x.SourceText.ToString().Replace("\r\n", "\n"))
            .SingleOrDefault();

    private static void AssertNotIntercepted(string calls, string extraTypes = "")
    {
        var run = Run(calls, extraTypes: extraTypes);
        AssertCompiles(run);
        Assert.Null(InterceptorsOf(run));
    }

    // Every interceptor makes one call into MediatorCalls: the call of its kind, with its message's key and whether the
    // message is exactly its static type.
    private static string Forwarding(string parameter, string call, string exact, string receiver = "requester") =>
        $"{parameter} message, global::System.Threading.CancellationToken cancellationToken = default) =>\n" +
        $"            global::Sharp.Mediator.Bindings.MediatorCalls.{call}({receiver}, message, {exact}, " +
        "cancellationToken);";

    private static string WhileSealed(string flag) => $"{DynamicCode} && {flag}";

    [Fact]
    public void EveryCallShape_GetsItsInterceptor()
    {
        var run = Run(
            """
            public static async Task All(IMediator mediator, IRequester requester, IPublisher publisher, CancellationToken token)
            {
                await mediator.Request(new Ping());
                await mediator.Request<Ping, int>(new Ping());
                await mediator.Request(new Noop());
                await publisher.Publish(new Signal(), token);
                await foreach (var item in mediator.RequestStream(new Count())) { }
                await foreach (var item in requester.RequestStream<Count, int>(new Count(), token)) { }
            }
            """);

        AssertCompiles(run);

        var text = InterceptorsOf(run)!;
        const string ping = "global::GeneratorFixture.Ping";
        const string noop = "global::GeneratorFixture.Noop";
        const string count = "global::GeneratorFixture.Count";
        const string signal = "global::GeneratorFixture.Signal";

        Assert.Contains(
            Forwarding("global::Sharp.Mediator.IRequest<int>", $"InferredRequest<{ping}, int, Key2>", "true"), text);

        Assert.Contains(Forwarding(ping, $"Request<{ping}, int, Key2>", "true"), text);
        Assert.Contains(Forwarding(noop, $"Request<{noop}, Key1>", "true"), text);
        Assert.Contains(Forwarding(signal, $"Publish<{signal}, Key3>", "true", "publisher"), text);

        Assert.Contains(
            Forwarding(
                "global::Sharp.Mediator.IStreamRequest<int>", $"InferredRequestStream<{count}, int, Key0>", "true"),
            text);

        Assert.Contains(Forwarding(count, $"RequestStream<{count}, int, Key0>", "true"), text);
        Assert.Equal(6, CountOccurrences(text, "InterceptsLocation("));
    }

    [Fact]
    // Nothing derives from a value type, nor from a sealed class compiled with the call, since unsealing it compiles
    // the call again, so their calls skip the runtime type check, and an open class's keep it. A class of another
    // assembly can be unsealed without recompiling this one, so its interceptors read once whether it is sealed when
    // the program runs, sealed today or not, and pass that. Calls of one such class share its flag.
    public void RuntimeTypeCheck_IsLeftOutOnlyWhereNothingCanDerive()
    {
        var library = CompileToReference("Library",
            """
            using Sharp.Mediator;
            namespace Library;
            public sealed record Remote : IRequest<int>;
            public record OpenRemote : IRequest<int>;
            """);

        var run = Run(
            """
            public static async Task All(IMediator mediator)
            {
                await mediator.Request<Value, int>(new Value());
                await mediator.Request<Open, int>(new Open());
                await mediator.Request<Library.Remote, int>(new Library.Remote());
                await mediator.Request(new Library.Remote());
                await mediator.Request<Library.OpenRemote, int>(new Library.OpenRemote());
            }
            """,
            references: [library]);

        AssertCompiles(run);

        var text = InterceptorsOf(run)!;
        const string value = "global::GeneratorFixture.Value";
        const string open = "global::GeneratorFixture.Open";
        const string remote = "global::Library.Remote";
        const string openRemote = "global::Library.OpenRemote";
        const string inferred = "global::Sharp.Mediator.IRequest<int>";

        Assert.Contains(Forwarding(value, $"Request<{value}, int, Key1>", "true"), text);
        Assert.Contains(Forwarding(open, $"Request<{open}, int, Key0>", "false"), text);
        Assert.Contains($"private static readonly bool Sealed0 = typeof({openRemote}).IsSealed;", text);
        Assert.Contains($"private static readonly bool Sealed1 = typeof({remote}).IsSealed;", text);
        Assert.Equal(2, CountOccurrences(text, "private static readonly bool"));
        Assert.Contains(Forwarding(remote, $"Request<{remote}, int, Key3>", WhileSealed("Sealed1")), text);
        Assert.Contains(Forwarding(inferred, $"InferredRequest<{remote}, int, Key3>", WhileSealed("Sealed1")), text);
        Assert.Contains(Forwarding(openRemote, $"Request<{openRemote}, int, Key2>", WhileSealed("Sealed0")), text);
    }

    [Fact]
    // Every interceptor passes a struct of its message's own as the key, so that the call of each message is an
    // instance of its own, whose callee the JIT profiles apart from every other message's: one key for each message.
    public void Interceptors_PassAKeyOfTheirMessagesOwn()
    {
        var run = Run(
            """
            public static async Task All(IPublisher publisher, IMediator mediator)
            {
                await publisher.Publish(new Signal());
                await mediator.Publish(new Signal());
                await publisher.Publish(new Pulse());
                await mediator.Request(new Ping());
            }
            """,
            extraTypes: """
                        public readonly record struct Pulse : IEvent;

                        """);

        AssertCompiles(run);

        var text = InterceptorsOf(run)!;
        const string ping = "global::GeneratorFixture.Ping";
        const string pulse = "global::GeneratorFixture.Pulse";
        const string signal = "global::GeneratorFixture.Signal";

        Assert.Equal(3, CountOccurrences(text, "private struct Key"));
        Assert.Contains("private struct Key0 { }", text);
        Assert.Contains("private struct Key1 { }", text);
        Assert.Contains("private struct Key2 { }", text);

        Assert.Contains(
            Forwarding("global::Sharp.Mediator.IRequest<int>", $"InferredRequest<{ping}, int, Key0>", "true"), text);

        Assert.Contains(Forwarding(pulse, $"Publish<{pulse}, Key1>", "true", "publisher"), text);
        Assert.Contains(Forwarding(signal, $"Publish<{signal}, Key2>", "true", "publisher"), text);
        Assert.Equal(1, CountOccurrences(text, $"Publish<{signal}, "));
    }

    [Fact]
    // An event interceptor leaves the runtime type check out by the same rules, and a class of another assembly gets a
    // flag as a request does.
    public void EventRuntimeTypeCheck_IsLeftOutOnlyWhereNothingCanDerive()
    {
        var library = CompileToReference("Library",
            """
            using Sharp.Mediator;
            namespace Library;
            public sealed record RemoteSignal : IEvent;
            public record OpenRemoteSignal : IEvent;
            """);

        var run = Run(
            """
            public static async Task All(IPublisher publisher)
            {
                await publisher.Publish(new Signal());
                await publisher.Publish(new OpenSignal());
                await publisher.Publish(new Pulse());
                await publisher.Publish(new Library.RemoteSignal());
                await publisher.Publish(new Library.OpenRemoteSignal());
            }
            """,
            references: [library],
            extraTypes: """
                        public record OpenSignal : IEvent;
                        public readonly record struct Pulse : IEvent;

                        """);

        AssertCompiles(run);

        var text = InterceptorsOf(run)!;
        const string signal = "global::GeneratorFixture.Signal";
        const string openSignal = "global::GeneratorFixture.OpenSignal";
        const string pulse = "global::GeneratorFixture.Pulse";
        const string remote = "global::Library.RemoteSignal";
        const string openRemote = "global::Library.OpenRemoteSignal";

        Assert.Contains(Forwarding(signal, $"Publish<{signal}, Key2>", "true", "publisher"), text);
        Assert.Contains(Forwarding(openSignal, $"Publish<{openSignal}, Key0>", "false", "publisher"), text);
        Assert.Contains(Forwarding(pulse, $"Publish<{pulse}, Key1>", "true", "publisher"), text);
        Assert.Contains($"private static readonly bool Sealed0 = typeof({openRemote}).IsSealed;", text);
        Assert.Contains($"private static readonly bool Sealed1 = typeof({remote}).IsSealed;", text);
        Assert.Equal(2, CountOccurrences(text, "private static readonly bool"));
        Assert.Contains(Forwarding(remote, $"Publish<{remote}, Key4>", WhileSealed("Sealed1"), "publisher"), text);

        Assert.Contains(
            Forwarding(openRemote, $"Publish<{openRemote}, Key3>", WhileSealed("Sealed0"), "publisher"), text);

        Assert.Equal(5, CountOccurrences(text, "InterceptsLocation("));
    }

    [Fact]
    // Only an expression tree keeps a call as written: a lambda that becomes a delegate, and a query over IEnumerable,
    // still compile to calls.
    public void CallsInDelegatesAndInMemoryQueries_AreIntercepted()
    {
        var run = Run(
            """
            public static System.Func<IMediator, ValueTask<int>> Lambda() => m => m.Request<Ping, int>(new Ping(), default);

            public static IEnumerable<ValueTask<int>> Query(IEnumerable<IMediator> source) =>
                from m in source select m.Request<Ping, int>(new Ping(), default);
            """);

        AssertCompiles(run);
        Assert.Equal(2, CountOccurrences(InterceptorsOf(run)!, "InterceptsLocation("));
    }

    [Fact]
    public void ReceiverConstrainedToAClass_IsIntercepted()
    {
        var run = Run(
            "public static ValueTask<int> Call<TMediator>(TMediator mediator) where TMediator : class, IMediator => mediator.Request(new Ping());");

        AssertCompiles(run);
        Assert.NotNull(InterceptorsOf(run));
    }

    [Fact]
    public void CallsOfOneMessage_ShareOneInterceptor()
    {
        var run = Run(
            """
            public static async Task All(IMediator mediator)
            {
                await mediator.Request(new Ping());
                await mediator.Request(new Ping());
                await mediator.Request(new Open());
            }
            """);

        AssertCompiles(run);

        var text = InterceptorsOf(run)!;
        Assert.Equal(3, CountOccurrences(text, "InterceptsLocation("));
        Assert.Equal(2, CountOccurrences(text, "public static "));
    }

    [Fact]
    public void OpenClass_IsInterceptedBehindARuntimeCheck()
    {
        const string open = "global::GeneratorFixture.Open";
        var run = Run("public static ValueTask<int> Call(IMediator mediator, Open message) => mediator.Request(message);");
        AssertCompiles(run);

        Assert.Contains(
            Forwarding("global::Sharp.Mediator.IRequest<int>", $"InferredRequest<{open}, int, Key0>", "false"),
            InterceptorsOf(run));
    }

    [Fact]
    // A named call can name an interface as its message: one of the application's own, or IRequest<T> and
    // IStreamRequest<T> themselves. No instance is exactly an interface, so the call always takes the uncached route,
    // which still skips the generic virtual call.
    public void NamedCallOnAnInterface_IsIntercepted()
    {
        var run = Run(
            """
            public static async Task All(IMediator mediator, IQuery query, IRequest<int> request, IStreamRequest<int> stream)
            {
                await mediator.Request<IQuery, int>(query);
                await mediator.Request<IRequest<int>, int>(request);
                await foreach (var item in mediator.RequestStream<IStreamRequest<int>, int>(stream)) { }
            }
            """,
            extraTypes: """
                        public interface IQuery : IRequest<int>;

                        """);

        AssertCompiles(run);

        var text = InterceptorsOf(run)!;
        const string query = "global::GeneratorFixture.IQuery";
        const string request = "global::Sharp.Mediator.IRequest<int>";
        const string stream = "global::Sharp.Mediator.IStreamRequest<int>";

        Assert.Contains(Forwarding(query, $"Request<{query}, int, Key0>", "false"), text);
        Assert.Contains(Forwarding(request, $"Request<{request}, int, Key1>", "false"), text);
        Assert.Contains(Forwarding(stream, $"RequestStream<{stream}, int, Key2>", "false"), text);
    }

    [Fact]
    public void NullableResponse_MatchesTheInterceptedSignature()
    {
        var run = Run("public static ValueTask<string?> Call(IMediator mediator) => mediator.Request(new Named());");
        AssertCompiles(run);
        Assert.Contains("#nullable disable", InterceptorsOf(run));
    }

    [Fact]
    public void ArrayResponse_InClosedGenericMessage_IsIntercepted()
    {
        var run = Run("public static ValueTask<int[]> Call(IMediator mediator) => mediator.Request(new Wrapped<int>.Inner());",
            extraTypes:
            """
            public static class Wrapped<T>
            {
                public sealed record Inner : IRequest<T[]>;
            }

            public sealed class WrappedHandler : IRequestHandler<Wrapped<int>.Inner, int[]>
            {
                public ValueTask<int[]> Handle(Wrapped<int>.Inner message, CancellationToken cancellationToken) => new(new int[0]);
            }

            """);

        AssertCompiles(run);
        Assert.Contains("Wrapped<int>.Inner, int[]", InterceptorsOf(run));
    }

    [Fact]
    public void DynamicResponse_IsIntercepted()
    {
        var run = Run("public static ValueTask<dynamic> Call(IMediator mediator) => mediator.Request<dynamic>(new Loose());",
            extraTypes:
            """
            public sealed record Loose : IRequest<object>;

            """);

        AssertCompiles(run);
        Assert.Contains("ValueTask<dynamic>", InterceptorsOf(run));
    }

    [Fact]
    public void ObsoleteAndExperimentalMessages_LeaveNoDiagnosticInTheInterceptors()
    {
        var run = Run(
            """
            // The caller uses the obsolete and experimental messages knowingly; the interceptors must add no diagnostic.
            #pragma warning disable CS0618, TRIAL0001
            public static ValueTask<int> Retire(IMediator mediator) => mediator.Request(new Retired());
            public static ValueTask<int> Try(IMediator mediator) => mediator.Request(new Trial());
            #pragma warning restore CS0618, TRIAL0001
            """,
            extraTypes:
            """
            [System.Obsolete("retired")]
            public sealed record Retired : IRequest<int>;

            [System.Diagnostics.CodeAnalysis.Experimental("TRIAL0001")]
            public sealed record Trial : IRequest<int>;

            """);

        AssertCompiles(run);
        Assert.NotNull(InterceptorsOf(run));
    }

    [Fact]
    // An error-level [Obsolete] is an error everywhere outside an obsolete member, which no pragma suppresses.
    // Calling such a message from retired code is valid, and its interceptor has to be too. Nothing else may warn
    // because of it.
    public void ErrorLevelObsoleteMessage_IsInterceptedWithoutDiagnostics()
    {
        var run = Run(
            """
            [System.Obsolete("retired", error: true)]
            public static ValueTask<int> Retire(IMediator mediator) => mediator.Request(new Removed());
            public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Ping());
            """,
            extraTypes:
            """
            [System.Obsolete("removed", error: true)]
            public sealed record Removed : IRequest<int>;

            """);

        AssertCompiles(run);
        Assert.Contains("global::GeneratorFixture.Removed", InterceptorsOf(run));
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), static x => x.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void WithoutTheCompilerSwitch_NothingIsIntercepted()
    {
        var run = Run(
            "public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Ping());",
            new CSharpParseOptions(LanguageVersion.Preview));

        AssertCompiles(run);
        Assert.Null(InterceptorsOf(run));
    }

    [Fact]
    public void SwitchNamingOnlyAnotherNamespace_InterceptsNothing()
    {
        var run = Run(
            "public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Ping());",
            new CSharpParseOptions(LanguageVersion.Preview).WithFeatures([new("InterceptorsNamespaces", "Other.Namespace")]));

        AssertCompiles(run);
        Assert.Null(InterceptorsOf(run));
    }

    [Fact]
    public void LanguageVersionWithoutFileTypes_InterceptsNothing()
    {
        var run = Run(
            "public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Ping());",
            Enabled.WithLanguageVersion(LanguageVersion.CSharp10));

        Assert.Null(InterceptorsOf(run));
    }

    [Fact]
    public void DesignTimeBuild_EmitsNoInterceptors()
    {
        var run = RunGenerator(
            Messages + "public static class Calls { public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Ping()); }",
            parseOptions: Enabled, disabledOutputs: IncrementalGeneratorOutputKind.Implementation);

        Assert.Null(InterceptorsOf(run));
        Assert.Contains(Assert.Single(run.Result.Results).GeneratedSources, static x => x.HintName == "SharpMediator.Registrations.g.cs");
    }

    [Theory]
    [InlineData("object overload of Request", "public static ValueTask<object?> Call(IMediator mediator) => mediator.Request((object) new Ping());")]
    [InlineData("object overload of RequestStream",
        "public static IAsyncEnumerable<object?> Call(IMediator mediator) => mediator.RequestStream((object) new Count());")]
    [InlineData("object overload of Publish", "public static ValueTask Call(IMediator mediator) => mediator.Publish((object) new Signal());")]
    [InlineData("publisher override", "public static ValueTask Call(IMediator mediator) => mediator.Publish(new Signal(), publisher: null);")]
    [InlineData("interface argument", "public static ValueTask<int> Call(IMediator mediator, IRequest<int> message) => mediator.Request(message);")]
    [InlineData("abstract argument", "public static ValueTask<int> Call(IMediator mediator, Shape message) => mediator.Request(message);")]
    [InlineData("value-type argument, boxed before the call",
        "public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Value());")]
    [InlineData("null argument", "public static ValueTask<int> Call(IMediator mediator) => mediator.Request<int>(null!);")]
    [InlineData("named message argument", "public static ValueTask<int> Call(IMediator mediator) => mediator.Request(message: new Ping());")]
    [InlineData("another method with the same name", "public static void Call(Other other) => other.Request(new Ping());")]
    [InlineData("expression tree",
        "public static System.Linq.Expressions.Expression<System.Func<IMediator, ValueTask<int>>> Call() => m => m.Request<Ping, int>(new Ping(), default);")]
    [InlineData("lambda inside an expression tree",
        "public static System.Linq.Expressions.Expression<System.Func<IMediator, System.Func<ValueTask<int>>>> Call() => m => () => m.Request(new Ping(), default);")]
    [InlineData("lambda converted to LambdaExpression",
        "public static System.Linq.Expressions.LambdaExpression Call() => (IMediator m) => m.Publish(new Signal(), default);")]
    [InlineData("query clause over IQueryable",
        "public static IQueryable<ValueTask> Call(IQueryable<IMediator> source) => from m in source select m.Request(new Noop(), default);")]
    [InlineData("value-type receiver",
        "public static ValueTask Call<TPublisher>(TPublisher publisher) where TPublisher : struct, IPublisher => publisher.Publish(new Signal());")]
    [InlineData("receiver that may be a value type",
        "public static ValueTask<int> Call<TRequester>(TRequester requester) where TRequester : IRequester => requester.Request<Ping, int>(new Ping());")]
    [InlineData("receiver that may be a ref struct",
        "public static ValueTask<int> Call<TRequester>(TRequester requester) where TRequester : IRequester, allows ref struct => requester.Request<Ping, int>(new Ping());")]
    public void CallShape_IsLeftAlone(string _, string call) =>
        AssertNotIntercepted(call,
            """
            public sealed record Holder<T> : IRequest<T>;

            public sealed class Other
            {
                public void Request(Ping message) { }
            }

            """);

    [Theory]
    [InlineData("mediator.Request<Missing, int>(null!)")]
    [InlineData("mediator.Request<Broken, Missing>(new Broken())")]
    [InlineData("mediator.Request()")]
    public void BrokenCallSite_DoesNotBreakTheGenerator(string call)
    {
        var run = Run($"public sealed record Broken : IRequest<Missing>;\npublic static object Call(IMediator mediator) => {call};");
        Assert.Null(InterceptorsOf(run));
        Assert.DoesNotContain(run.Diagnostics, static x => x.Id == "CS8785");
    }

    [Theory]
    [InlineData("mediator.Request<Nested, List<Missing>>(new Nested())")]
    [InlineData("mediator.Request(new Nested())")]
    // A message or response that only takes such a type as a type argument is a type generated code cannot write, so
    // the call gets a generic interceptor, which names none of its types: the compiler reports the missing type and
    // nothing in the generated code.
    public void CallWithAMissingTypeArgument_GetsAGenericInterceptor(string call)
    {
        var run = Run($"public sealed record Nested : IRequest<List<Missing>>;\npublic static object Call(IMediator mediator) => {call};");
        Assert.NotNull(InterceptorsOf(run));

        var errors = run.OutputCompilation.GetDiagnostics()
            .Where(static x => x.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.NotEmpty(errors);
        Assert.All(errors, static x => Assert.Equal("CS0246", x.Id));
        Assert.All(errors, static x => Assert.DoesNotContain(Interceptors, x.Location.SourceTree!.FilePath));
    }

    [Fact]
    // A call whose types generated code cannot write gets a generic interceptor of the intercepted method's type
    // parameters and constraints, which takes the call's type arguments as its own and skips the interface's generic
    // virtual call. It passes a key that every such call shares, and an inferred one, which names no message type,
    // takes the route that finds the runtime type. Every such call of one shape shares its interceptor.
    public void CallsFromGenericCode_GetAGenericInterceptorForEachShape()
    {
        var run = Run(
            """
            public static ValueTask Void<T>(IMediator mediator, T message) where T : IRequest => mediator.Request(message);
            public static ValueTask<int> Typed<T>(IMediator mediator, T message) where T : IRequest<int> => mediator.Request<T, int>(message);
            public static ValueTask<int> TypedAgain<T>(IRequester requester, T message) where T : IRequest<int> => requester.Request<T, int>(message);
            public static ValueTask<T> Inferred<T>(IMediator mediator, Holder<T> message) => mediator.Request(message);
            public static ValueTask Publish<T>(IPublisher publisher, T message) where T : IEvent => publisher.Publish(message);
            public static IAsyncEnumerable<int> Stream<T>(IMediator mediator, T message) where T : IStreamRequest<int> => mediator.RequestStream<T, int>(message);
            public static IAsyncEnumerable<T> InferredStream<T>(IMediator mediator, Flow<T> message) => mediator.RequestStream(message);
            """,
            extraTypes: """
                        public sealed record Holder<T> : IRequest<T>;
                        public sealed record Flow<T> : IStreamRequest<T>;

                        """);

        AssertCompiles(run);

        var text = InterceptorsOf(run)!;
        const string task = "global::System.Threading.Tasks.ValueTask";
        const string stream = "global::System.Collections.Generic.IAsyncEnumerable";

        Assert.Equal(7, CountOccurrences(text, "InterceptsLocation("));
        Assert.Equal(6, CountOccurrences(text, "public static "));

        Assert.Contains(
            Generic(
                task, "0<TRequest>", "TRequest", " where TRequest : global::Sharp.Mediator.IRequest",
                "Request<TRequest, UnnamedKey>"), text);

        Assert.Contains(
            Generic(
                $"{task}<TResponse>", "1<TRequest, TResponse>", "TRequest",
                " where TRequest : global::Sharp.Mediator.IRequest<TResponse>",
                "Request<TRequest, TResponse, UnnamedKey>"), text);

        Assert.Contains(
            Generic(
                $"{task}<TResponse>", "2<TResponse>", "global::Sharp.Mediator.IRequest<TResponse>", "",
                "InferredRequest<TResponse>", keyed: false), text);

        Assert.Contains(
            Generic(
                task, "3<TEvent>", "TEvent", " where TEvent : global::Sharp.Mediator.IEvent",
                "Publish<TEvent, UnnamedKey>", "this global::Sharp.Mediator.IPublisher publisher"), text);

        Assert.Contains(
            Generic(
                $"{stream}<TResponse>", "4<TRequest, TResponse>", "TRequest",
                " where TRequest : global::Sharp.Mediator.IStreamRequest<TResponse>",
                "RequestStream<TRequest, TResponse, UnnamedKey>"), text);

        Assert.Contains(
            Generic(
                $"{stream}<TResponse>", "5<TResponse>", "global::Sharp.Mediator.IStreamRequest<TResponse>", "",
                "InferredRequestStream<TResponse>", keyed: false), text);

        Assert.Contains("private struct UnnamedKey { }", text);
    }

    // A generic interceptor: its type parameters, the message's type, its constraints, and the call it forwards to,
    // which is told the message is not exact unless it takes no key.
    private static string Generic(
        string returns,
        string name,
        string message,
        string constraint,
        string forward,
        string receiver = "this global::Sharp.Mediator.IRequester requester",
        bool keyed = true
    ) => $"public static {returns} Call{name}({receiver}, {message} message, " +
        $"global::System.Threading.CancellationToken cancellationToken = default){constraint} =>\n" +
        $"            global::Sharp.Mediator.Bindings.MediatorCalls.{forward}({receiver.Split(' ')[^1]}, message, " +
        $"{(keyed ? "false, " : "")}cancellationToken);";

    // So does a call of a private message or response, of a private type argument of a containing type, or of a message
    // that only an extern alias reaches.
    private static void AssertGenericallyIntercepted(GeneratorRun run, string call)
    {
        AssertCompiles(run);

        var text = InterceptorsOf(run)!;
        Assert.Contains($"global::Sharp.Mediator.Bindings.MediatorCalls.{call};", text);
        Assert.Equal(1, CountOccurrences(text, "public static "));
    }

    [Fact]
    public void PrivateMessage_GetsAGenericInterceptor() =>
        AssertGenericallyIntercepted(
            Run(
                """
                private sealed record Hidden : IRequest<int>;

                public static ValueTask<int> Call(IMediator mediator) => mediator.Request<Hidden, int>(new Hidden());
                """),
            "Request<TRequest, TResponse, UnnamedKey>(requester, message, false, cancellationToken)");

    [Fact]
    public void PrivateResponse_GetsAGenericInterceptor() =>
        AssertGenericallyIntercepted(
            Run("""
                private sealed record Answer;

                private sealed record Hidden : IRequest<Answer>;

                private static ValueTask<Answer> Call(IMediator mediator) => mediator.Request(new Hidden());
                """),
            "InferredRequest<TResponse>(requester, message, cancellationToken)");

    [Fact]
    public void PrivateTypeArgumentOfAContainingType_GetsAGenericInterceptor() =>
        AssertGenericallyIntercepted(
            Run("""
                private sealed record Secret;

                private static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Generic<Secret>.Inner());
                """,
                extraTypes: """
                            public static class Generic<T>
                            {
                                public sealed record Inner : IRequest<int>;
                            }

                            """),
            "InferredRequest<TResponse>(requester, message, cancellationToken)");

    [Fact]
    public void MessageOnlyReachableThroughAnExternAlias_GetsAGenericInterceptor()
    {
        var aliased = CompileToReference("Aliased",
            """
            using Sharp.Mediator;
            namespace Library;
            public sealed record Remote : IRequest<int>;
            """).WithAliases(["lib"]);

        var run = RunGenerator(
            """
            extern alias lib;
            using System.Threading.Tasks;
            using Sharp.Mediator;

            public static class Calls
            {
                public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new lib::Library.Remote());
            }
            """,
            [aliased], parseOptions: Enabled);

        AssertGenericallyIntercepted(run, "InferredRequest<TResponse>(requester, message, cancellationToken)");
    }

    [Fact]
    public void AssemblyKeepingTheGlobalAlias_IsIntercepted()
    {
        var aliased = CompileToReference("Aliased",
            """
            using Sharp.Mediator;
            namespace Library;
            public sealed record Remote : IRequest<int>;
            """).WithAliases(["lib", "global"]);

        var run = RunGenerator(
            """
            using System.Threading.Tasks;
            using Sharp.Mediator;

            public static class Calls
            {
                public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Library.Remote());
            }
            """,
            [aliased], parseOptions: Enabled);

        AssertCompiles(run);
        Assert.NotNull(InterceptorsOf(run));
    }
}