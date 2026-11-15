using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using static Sharp.Mediator.SourceGenerator.Tests.Fixtures.GeneratorTestHarness;

namespace Sharp.Mediator.SourceGenerator.Tests.Diagnostics;

// Generator diagnostics, invalid-shape handling, and accessibility contracts.
public sealed class DiagnosticTests
{
    // ---- closed middleware that can never run -----------------------------------------------------------------------

    private const string ClosedMiddlewareFixture =
        """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Sharp.Mediator;
        namespace GeneratorFixture;

        public sealed record Save : IRequest;
        public sealed record Saved : IEvent;
        public sealed record Ping : IRequest<int>;
        public sealed record Tail : IStreamRequest<int>;

        public sealed class Handlers : IRequestHandler<Save>, IEventHandler<Saved>, IRequestHandler<Ping, int>, IStreamRequestHandler<Tail, int>
        {
            public ValueTask Handle(Save message, CancellationToken cancellationToken) => default;
            public ValueTask Handle(Saved message, CancellationToken cancellationToken) => default;
            public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => default;
            public IAsyncEnumerable<int> Handle(Tail request, CancellationToken cancellationToken) => throw null!;
        }
        """;

    // ---- language version -------------------------------------------------------------------------------------------

    private const string CSharp8Fixture =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Sharp.Mediator;
        namespace GeneratorFixture
        {
            public sealed class Ping : IRequest<int> { }

            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => new ValueTask<int>(1);
            }
        }
        """;
    // ---- diagnostics ------------------------------------------------------------------------------------------------

    [Fact]
    public void MultiResponseRequest_ReportsSmd001_WithLocation()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed class MultiResponseRequest : IRequest<int>, IRequest<string>;

            public sealed class IntHandler : IRequestHandler<MultiResponseRequest, int>
            {
                public ValueTask<int> Handle(MultiResponseRequest message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }

            public sealed class StringHandler : IRequestHandler<MultiResponseRequest, string>
            {
                public ValueTask<string> Handle(MultiResponseRequest message, CancellationToken cancellationToken) => ValueTask.FromResult("bad");
            }
            """;

        var run = RunGenerator(source);
        var diagnostic = Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD001"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("MultiResponseRequest", diagnostic.GetMessage());
        Assert.NotEqual(Location.None, diagnostic.Location);
    }

    [Fact]
    // A request is dispatched to the handler of its runtime type or of a base class, and a runtime type is never an
    // interface, so a request or stream handler declared for one can never run. An event reaches interface handlers.
    public void RequestAndStreamHandlersForAnInterface_ReportSmd008()
    {
        const string source =
            """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public interface IQuery : IRequest<int>;
            public interface ICommand : IRequest;
            public interface IFeed : IStreamRequest<int>;
            public interface INotice : IEvent;

            public sealed class QueryHandler : IRequestHandler<IQuery, int>
            {
                public ValueTask<int> Handle(IQuery message, CancellationToken cancellationToken) => new(1);
            }

            public sealed class CommandHandler : IRequestHandler<ICommand>
            {
                public ValueTask Handle(ICommand message, CancellationToken cancellationToken) => default;
            }

            public sealed class FeedHandler : IStreamRequestHandler<IFeed, int>
            {
                public async IAsyncEnumerable<int> Handle(IFeed message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    yield return 1;
                    await Task.CompletedTask;
                }
            }

            public sealed class NoticeHandler : IEventHandler<INotice>
            {
                public ValueTask Handle(INotice message, CancellationToken cancellationToken) => default;
            }
            """;

        var run = RunGenerator(source);
        var diagnostics = run.Diagnostics.Where(static x => x.Id == "SMD008").OrderBy(static x => x.GetMessage(), StringComparer.Ordinal).ToList();
        Assert.Equal(3, diagnostics.Count);
        Assert.All(diagnostics, static x => Assert.Equal(DiagnosticSeverity.Error, x.Severity));
        Assert.All(diagnostics, static x => Assert.NotEqual(Location.None, x.Location));
        Assert.Contains("'CommandHandler' handles interface 'ICommand'", diagnostics[0].GetMessage());
        Assert.Contains("'FeedHandler' handles interface 'IFeed'", diagnostics[1].GetMessage());
        Assert.Contains("'QueryHandler' handles interface 'IQuery'", diagnostics[2].GetMessage());
        Assert.DoesNotContain("IQuery", RegistrationsText(run));
        Assert.Contains("INotice", RegistrationsText(run));
    }

    [Fact]
    public void MultiResponseRequest_WithSingleDiscoveredHandler_ReportsSmd001()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed class MultiResponseRequest : IRequest<int>, IRequest<string>;

            public sealed class IntHandler : IRequestHandler<MultiResponseRequest, int>
            {
                public ValueTask<int> Handle(MultiResponseRequest message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }
            """;

        var run = RunGenerator(source);
        Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD001"));
    }

    [Fact]
    public void BothRequestInterfaces_ReportsSmd002_WithLocation()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed class DualRequest : IRequest, IRequest<string>;

            public sealed class DualVoidHandler : IRequestHandler<DualRequest>
            {
                public ValueTask Handle(DualRequest message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
            }
            """;

        var run = RunGenerator(source);
        var diagnostic = Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD002"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("DualRequest", diagnostic.GetMessage());
        Assert.NotEqual(Location.None, diagnostic.Location);
    }

    [Fact]
    public void MultiResponseStreamRequest_ReportsSmd004_WithLocation()
    {
        // The stream mirror of SMD001: without it, a multi-response stream request relied on the typed arm's TResponse
        // guard as its only safety net.
        const string source =
            """
            using System.Collections.Generic;
            using System.Threading;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed class MultiResponseCount : IStreamRequest<int>, IStreamRequest<string>;

            public sealed class IntCountHandler : IStreamRequestHandler<MultiResponseCount, int>
            {
                public async IAsyncEnumerable<int> Handle(MultiResponseCount message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    yield return 1;
                }
            }
            """;

        var run = RunGenerator(source);
        var diagnostic = Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD004"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("MultiResponseCount", diagnostic.GetMessage());
        Assert.NotEqual(Location.None, diagnostic.Location);
        Assert.DoesNotContain("MultiResponseCount", AllGeneratedText(run));
    }

    [Fact]
    // A message nested in a generic type is only as accessible as that type's arguments, at any depth. The check used
    // to look at the message's own arguments alone, so Box<Secret>.Message reached the generated registrations, which
    // named the private Host.Secret and broke the consumer build with CS0122 instead of reporting SMD003.
    public void MessageNestedInTypeWithPrivateArgument_ReportsSmd003WithoutBreakingCompilation()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public class Box<T>
            {
                public sealed class Message : IRequest<int>;

                public static class Inner
                {
                    public sealed class Deep : IRequest<int>;
                }
            }

            public sealed class OpenHandler : IRequestHandler<Box<int>.Message, int>
            {
                public ValueTask<int> Handle(Box<int>.Message message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }

            public class Host
            {
                private sealed class Secret;

                public sealed class Handler : IRequestHandler<Box<Secret>.Message, int>
                {
                    ValueTask<int> IRequestHandler<Box<Secret>.Message, int>.Handle(
                        Box<Secret>.Message message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                }

                public sealed class DeepHandler : IRequestHandler<Box<Secret>.Inner.Deep, int>
                {
                    ValueTask<int> IRequestHandler<Box<Secret>.Inner.Deep, int>.Handle(
                        Box<Secret>.Inner.Deep message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                }
            }
            """;

        var run = RunGenerator(source);
        var reported = run.Diagnostics.Where(static x => x.Id == "SMD003").Select(static x => x.GetMessage()).ToList();
        Assert.Equal(2, reported.Count);

        Assert.Contains(reported,
            static x => x.StartsWith("Message type 'Message'", StringComparison.Ordinal) &&
                x.Contains("Handler 'Handler'", StringComparison.Ordinal));

        Assert.Contains(reported,
            static x => x.StartsWith("Message type 'Deep'", StringComparison.Ordinal) &&
                x.Contains("Handler 'DeepHandler'", StringComparison.Ordinal));

        AssertCompiles(run);
        Assert.DoesNotContain("Secret", AllGeneratedText(run));
        Assert.Contains("global::GeneratorFixture.Box<int>.Message", RegistrationsText(run));
    }

    [Fact]
    public void PrivateNestedMessage_ReportsSmd003WithoutBreakingCompilation()
    {
        // Regression: a private nested message (valid user code) used to leak into the generated dispatcher and break
        // the consumer build with CS0122. Now it is excluded with a warning.
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Visible : IRequest;

            public sealed class VisibleHandler : IRequestHandler<Visible>
            {
                public ValueTask Handle(Visible message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
            }

            public static class Container
            {
                private sealed record HiddenRequest : IRequest;

                private sealed class HiddenHandler : IRequestHandler<HiddenRequest>
                {
                    public ValueTask Handle(HiddenRequest message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                }
            }
            """;

        var run = RunGenerator(source);
        var diagnostic = Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD003"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("HiddenRequest", diagnostic.GetMessage());
        AssertCompiles(run);
        Assert.DoesNotContain("HiddenRequest", AllGeneratedText(run));
    }

    [Fact]
    public void NonMirrorOpenGenericMiddleware_ReportsSmd011()
    {
        // Type parameters that do not mirror the middleware interface's arguments (here: a fixed response type) are
        // outside the supported open-generic contract and must be registered as closed types.
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Ping : IRequest<int>;

            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }

            public sealed class FixedResponseMiddleware<TMessage> : IMiddleware<TMessage, int> where TMessage : IMessage<int>
            {
                public ValueTask<int> Handle(TMessage message, MessageHandle<TMessage, int> next, CancellationToken cancellationToken) =>
                    next(message, cancellationToken);
            }
            """;

        var run = RunGenerator(source);
        var diagnostic = Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD011"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("FixedResponseMiddleware", diagnostic.GetMessage());
        AssertCompiles(run);
        Assert.DoesNotContain("FixedResponseMiddleware", RegistrationsText(run));
    }

    [Fact]
    public void NonMirrorOpenGenericMiddleware_RuntimeRegistrationRejectsEveryUnsupportedShape()
    {
        const string source =
            """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace RuntimeFixture;

            public sealed class FixedResponse<TMessage> : IMiddleware<TMessage, int> where TMessage : IMessage<int>
            {
                public ValueTask<int> Handle(TMessage message, MessageHandle<TMessage, int> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            public sealed class Reversed<TResponse, TMessage> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            public sealed class Unused<TMessage, TResponse, TUnused> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            public sealed class Container<TOuter>
            {
                public sealed class Nested<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
                {
                    public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) => next(message, cancellationToken);
                }
            }
            public sealed class FixedStreamResponse<TRequest> : IStreamMiddleware<TRequest, int> where TRequest : IStreamRequest<int>
            {
                public IAsyncEnumerable<int> Handle(TRequest message, StreamMessageHandle<TRequest, int> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            """;

        var assembly = CompileToAssembly("RuntimeFixture", source);
        foreach (var name in new[]
                 {
                     "RuntimeFixture.FixedResponse`1", "RuntimeFixture.Reversed`2", "RuntimeFixture.Unused`3",
                     "RuntimeFixture.Container`1+Nested`1"
                 })
        {
            var middlewareType = assembly.GetType(name, true)!;
            var exception = Assert.Throws<InvalidOperationException>(() =>
                new ServiceCollection().AddMediator(x => x.AddMiddleware(middlewareType)));

            Assert.Contains("one-for-one in the same order", exception.Message);
            Assert.Contains("Register closed middleware types", exception.Message);
        }

        var streamType = assembly.GetType("RuntimeFixture.FixedStreamResponse`1", true)!;
        var streamException = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddMediator(x => x.AddMiddleware(streamType)));

        Assert.Contains("one-for-one in the same order", streamException.Message);
        Assert.Contains("Register closed middleware types", streamException.Message);
    }

    [Fact]
    public void RecordStructRequestAndStream_CompileWithInterfaceUnboxing()
    {
        const string source =
            """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public readonly record struct Ping(int Value) : IRequest<int>;
            public readonly record struct Count(int Value) : IStreamRequest<int>;

            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) =>
                    ValueTask.FromResult(message.Value);
            }

            public sealed class CountHandler : IStreamRequestHandler<Count, int>
            {
                public async IAsyncEnumerable<int> Handle(
                    Count message,
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    yield return message.Value;
                }
            }
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var generated = RegistrationsText(run);
        Assert.Contains("CreateRequestRoute<global::GeneratorFixture.Ping, int>", generated);
        Assert.Contains("CreateStreamRoute<global::GeneratorFixture.Count, int>", generated);
    }

    [Fact]
    public void AbstractResponse_DoesNotSatisfyNewConstraint()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Ping : IRequest<AbstractResponse>;
            public abstract class AbstractResponse
            {
                public AbstractResponse() { }
            }

            public sealed class PingHandler : IRequestHandler<Ping, AbstractResponse>
            {
                public ValueTask<AbstractResponse> Handle(Ping message, CancellationToken cancellationToken) =>
                    ValueTask.FromResult<AbstractResponse>(null!);
            }

            public sealed class ConstructingMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                where TMessage : IMessage<TResponse>
                where TResponse : new()
            {
                public ValueTask<TResponse> Handle(
                    TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                    next(message, cancellationToken);
            }
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        Assert.DoesNotContain(
            "ConstructingMiddleware<global::GeneratorFixture.Ping, global::GeneratorFixture.AbstractResponse>",
            RegistrationsText(run));
    }

    [Fact]
    public void NestedGenericMiddleware_ReportsSmd011()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Ping : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }

            public sealed class Container<TOuter>
            {
                public sealed class NestedMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                    where TMessage : IMessage<TResponse>
                {
                    public ValueTask<TResponse> Handle(
                        TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                        next(message, cancellationToken);
                }
            }
            """;

        var run = RunGenerator(source);
        Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD011"));
        AssertCompiles(run);
        Assert.DoesNotContain("NestedMiddleware", RegistrationsText(run));
    }

    [Fact]
    public void PrivateOpenGenericMiddleware_ReportsSmd010WithoutClosingIt()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Ping : IRequest<int>;

            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }

            public static class Pipeline
            {
                private sealed class HiddenMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                    where TMessage : IMessage<TResponse>
                {
                    public ValueTask<TResponse> Handle(
                        TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                        next(message, cancellationToken);
                }
            }
            """;

        var run = RunGenerator(source);
        var diagnostic = Assert.Single(run.Diagnostics, static x => x.Id == "SMD010");
        Assert.Contains("'HiddenMiddleware'", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal("GeneratorFixture.cs", diagnostic.Location.SourceTree?.FilePath);
        AssertCompiles(run);
        Assert.DoesNotContain("HiddenMiddleware", RegistrationsText(run));
    }

    [Fact]
    public void MiddlewareOnlyAssembly_ReportsDiagnosticsWithoutEmittingRegistrar()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public static class Container
            {
                private sealed class FixedResponseMiddleware<TMessage> : IMiddleware<TMessage, int>
                    where TMessage : IMessage<int>
                {
                    public ValueTask<int> Handle(
                        TMessage message, MessageHandle<TMessage, int> next, CancellationToken cancellationToken) =>
                        next(message, cancellationToken);
                }
            }
            """;

        var run = RunGenerator(source);
        Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD011"));
        Assert.DoesNotContain(run.Diagnostics, static x => x.Id == "SMD010");
        AssertCompiles(run);
        AssertOnlyHandlerRegistrarMarker(run);
    }

    [Fact]
    public void FileLocalHandlersAndArrayResponses_AreInspectedWithoutLeakingInaccessibleTypes()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            file sealed record FileRequest : IRequest;
            file sealed class FileHandler : IRequestHandler<FileRequest>
            {
                public ValueTask Handle(FileRequest message, CancellationToken cancellationToken) => default;
            }

            internal sealed class Payload;
            internal sealed record ArrayRequest : IRequest<Payload[]>;
            internal sealed class ArrayHandler : IRequestHandler<ArrayRequest, Payload[]>
            {
                public ValueTask<Payload[]> Handle(ArrayRequest message, CancellationToken cancellationToken) =>
                    ValueTask.FromResult(System.Array.Empty<Payload>());
            }
            """;

        var run = RunGenerator(source);
        Assert.Contains(run.Diagnostics, static diagnostic => diagnostic.Id == "SMD003");
        Assert.DoesNotContain("FileRequest", AllGeneratedText(run));
        AssertCompiles(run);
    }

    [Fact]
    public void ErroneousDynamicResponse_DoesNotCrashTheGenerator()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record DynamicRequest : IRequest<dynamic>;
            public sealed class DynamicHandler : IRequestHandler<DynamicRequest, dynamic>
            {
                public ValueTask<dynamic> Handle(DynamicRequest message, CancellationToken cancellationToken) =>
                    ValueTask.FromResult<dynamic>(new object());
            }
            """;

        var run = RunGenerator(source);
        Assert.DoesNotContain(run.Diagnostics, static diagnostic => diagnostic.Id == "CS8785");
    }

    [Fact]
    public void MiddlewareConstraintContainingTypeParameterArray_IsAnalyzed()
    {
        const string source =
            """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed class ArrayConstraintMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                where TMessage : IMessage<TResponse>
                where TResponse : IEnumerable<TMessage[]>
            {
                public ValueTask<TResponse> Handle(
                    TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                    next(message, cancellationToken);
            }
            """;

        var run = RunGenerator(source);
        Assert.DoesNotContain(run.Diagnostics, static diagnostic => diagnostic.Id == "SMD011");
        AssertCompiles(run);
    }

    [Fact]
    public void ArityOneMiddleware_EmitsBothVoidRequestAndEventClosureArms()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Command : IRequest;
            public sealed record Signal : IEvent;
            public sealed class CommandHandler : IRequestHandler<Command>
            {
                public ValueTask Handle(Command message, CancellationToken cancellationToken) => default;
            }
            public sealed class SignalHandler : IEventHandler<Signal>
            {
                public ValueTask Handle(Signal message, CancellationToken cancellationToken) => default;
            }
            public sealed class Pipeline<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
            {
                public ValueTask Handle(
                    TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
                    next(message, cancellationToken);
            }
            """;

        var registrations = RegistrationsText(RunGenerator(source));
        Assert.Contains("Pipeline<global::GeneratorFixture.Command>", registrations);
        Assert.Contains("Pipeline<global::GeneratorFixture.Signal>", registrations);
    }

    [Fact]
    public void GeneratorFilters_UnsuitableMiddlewareAndHandlerTypes_EmitOnlyTheRegistrarMarker()
    {
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Ping : IRequest;
            public sealed class GenericUtility<T> : IDisposable { public void Dispose() { } }
            public abstract class AbstractPipeline<T> : IMiddleware<T> where T : IMessage
            {
                public abstract ValueTask Handle(T message, MessageHandle<T> next, CancellationToken cancellationToken);
            }
            public readonly record struct StructPipeline<T>() : IMiddleware<T> where T : IMessage
            {
                public ValueTask Handle(T message, MessageHandle<T> next, CancellationToken cancellationToken) =>
                    next(message, cancellationToken);
            }
            public sealed class NonHandler : IDisposable { public void Dispose() { } }
            public abstract class AbstractHandler : IRequestHandler<Ping>
            {
                public abstract ValueTask Handle(Ping message, CancellationToken cancellationToken);
            }
            public sealed class GenericHandler<T> : IRequestHandler<Ping>
            {
                public ValueTask Handle(Ping message, CancellationToken cancellationToken) => default;
            }
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        AssertOnlyHandlerRegistrarMarker(run);
    }

    [Fact]
    public void MultipleRequestAndStreamHandlers_ReportSmd005()
    {
        const string source =
            """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Ping : IRequest<int>;
            public sealed record Count : IStreamRequest<int>;

            public sealed class PingHandlerA : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }
            public sealed class PingHandlerB : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(2);
            }
            public sealed class CountHandlerA : IStreamRequestHandler<Count, int>
            {
                public async IAsyncEnumerable<int> Handle(Count message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    yield return 1;
                }
            }
            public sealed class CountHandlerB : IStreamRequestHandler<Count, int>
            {
                public async IAsyncEnumerable<int> Handle(Count message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    yield return 2;
                }
            }
            """;

        var run = RunGenerator(source);
        var diagnostics = run.Diagnostics.Where(static x => x.Id == "SMD005").ToList();
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, static diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Fact]
    public void GeneratedNamespaces_AreAssemblySpecific_AvoidingCs0436AcrossIvtAssemblies()
    {
        const string firstSource =
            """
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            [assembly: InternalsVisibleTo("Consumer.B")]
            namespace First;

            internal sealed record Ping : IRequest<int>;
            internal sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }
            """;

        const string secondSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace Second;

            internal sealed record Pong : IRequest<int>;
            internal sealed class PongHandler : IRequestHandler<Pong, int>
            {
                public ValueTask<int> Handle(Pong message, CancellationToken cancellationToken) => ValueTask.FromResult(2);
            }
            """;

        var firstReference = CompileGeneratedToReference("Library.A", firstSource);
        var run = RunGenerator(secondSource, [firstReference], "Consumer.B");
        AssertCompiles(run);
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), static x => x.Id == "CS0436");
        Assert.Contains("namespace Sharp.Mediator.Generated.AConsumer_002EB", AllGeneratedText(run));
    }

    [Fact]
    public void ReferencedInternalAndProtectedInternalMiddleware_RequireIvt_PublicDoesNot()
    {
        // Middleware another assembly keeps to itself is that assembly's business: it is left out without a warning
        // here, and closed here only when InternalsVisibleTo lets generated code name it.
        const string packageWithoutIvt =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace Package;

            public sealed class PublicMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                where TMessage : IMessage<TResponse>
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            internal sealed class InternalMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                where TMessage : IMessage<TResponse>
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            public class Container
            {
                protected internal sealed class ProtectedInternalMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                    where TMessage : IMessage<TResponse>
                {
                    public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
                }
            }
            """;

        var packageWithIvt = packageWithoutIvt.Replace(
            "namespace Package;",
            "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Consumer\")]\nnamespace Package;",
            StringComparison.Ordinal);

        const string consumerSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace ConsumerApp;

            public sealed record Ping : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }
            """;

        var withoutIvt = RunGenerator(
            consumerSource, [CompileToReference("Package.NoIvt", packageWithoutIvt)], "Consumer");

        AssertCompiles(withoutIvt);
        Assert.DoesNotContain(withoutIvt.Diagnostics, static x => x.Id == "SMD010");
        Assert.Contains("PublicMiddleware<global::ConsumerApp.Ping, int>", RegistrationsText(withoutIvt));
        Assert.DoesNotContain("InternalMiddleware<global::ConsumerApp.Ping, int>", RegistrationsText(withoutIvt));
        Assert.DoesNotContain("ProtectedInternalMiddleware<global::ConsumerApp.Ping, int>", RegistrationsText(withoutIvt));

        var withIvt = RunGenerator(
            consumerSource, [CompileToReference("Package.WithIvt", packageWithIvt)], "Consumer");

        AssertCompiles(withIvt);
        Assert.DoesNotContain(withIvt.Diagnostics, static x => x.Id == "SMD010");
        Assert.Contains("InternalMiddleware<global::ConsumerApp.Ping, int>", RegistrationsText(withIvt));
        Assert.Contains("ProtectedInternalMiddleware<global::ConsumerApp.Ping, int>", RegistrationsText(withIvt));
    }

    // ---- suppression ------------------------------------------------------------------------------------------------

    [Fact]
    public void Diagnostics_AreAnchoredToTheCompilationsSyntaxTree()
    {
        // #pragma and .editorconfig severity are resolved through the diagnostic's syntax tree. Location.Create with a
        // path yields an external location with no tree, which left a project-wide NoWarn as the only suppression.
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public static class Container
            {
                private sealed record Hidden : IRequest<int>;

                private sealed class HiddenHandler : IRequestHandler<Hidden, int>
                {
                    public ValueTask<int> Handle(Hidden message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                }
            }

            public sealed class Swapped<TResponse, TMessage> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                    next(message, cancellationToken);
            }
            """;

        var run = RunGenerator(source);
        foreach (var id in new[] { "SMD003", "SMD011" })
        {
            var diagnostic = Assert.Single(run.Diagnostics.Where(x => x.Id == id));
            Assert.Equal("GeneratorFixture.cs", diagnostic.Location.SourceTree?.FilePath);
            Assert.True(diagnostic.Location.GetLineSpan().StartLinePosition.Line > 0);
        }
    }

    [Fact]
    public void ClosedMiddleware_ForAPipelineItsMessageNeverUses_ReportsSmd012()
    {
        // IMiddleware<Ping> compiles because the constraint is only IMessage, and registers without complaint, but the
        // response pipeline resolves IMiddleware<Ping, int>. Nothing else tells the author it never runs.
        const string source = ClosedMiddlewareFixture +
            """

            public sealed partial class VoidShapeOverResponseRequest : IMiddleware<Ping>
            {
                public ValueTask Handle(Ping message, MessageHandle<Ping> next, CancellationToken cancellationToken) => default;
            }

            public sealed partial class VoidShapeOverResponseRequest : System.IDisposable
            {
                public void Dispose() { }
            }

            public sealed class ResponseShapeOverStream : IMiddleware<Tail, int>
            {
                public ValueTask<int> Handle(Tail message, MessageHandle<Tail, int> next, CancellationToken cancellationToken) => default;
            }
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var diagnostics = run.Diagnostics.Where(static x => x.Id == "SMD012").ToList();
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, static x => Assert.Equal(DiagnosticSeverity.Warning, x.Severity));
        Assert.Contains(diagnostics, static x => x.GetMessage().Contains("ResponseShapeOverStream", StringComparison.Ordinal));
        Assert.Contains(diagnostics, static x => x.GetMessage().Contains("VoidShapeOverResponseRequest", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedMiddleware_ForItsMessagesPipeline_ReportsNothing()
    {
        const string source = ClosedMiddlewareFixture +
            """

            public sealed class OverVoidRequest : IMiddleware<Save>
            {
                public ValueTask Handle(Save message, MessageHandle<Save> next, CancellationToken cancellationToken) => default;
            }

            public sealed class OverEvent : IMiddleware<Saved>
            {
                public ValueTask Handle(Saved message, MessageHandle<Saved> next, CancellationToken cancellationToken) => default;
            }

            public sealed class OverResponseRequest : IMiddleware<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, MessageHandle<Ping, int> next, CancellationToken cancellationToken) => default;
            }

            public sealed class OverStream : IStreamMiddleware<Tail, int>
            {
                public IAsyncEnumerable<int> Handle(Tail message, StreamMessageHandle<Tail, int> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }

            public abstract class AbstractIsNotRegistrable : IMiddleware<Ping>
            {
                public abstract ValueTask Handle(Ping message, MessageHandle<Ping> next, CancellationToken cancellationToken);
            }
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        Assert.DoesNotContain(run.Diagnostics, static x => x.Id == "SMD012");
    }

    [Fact]
    // Diagnostics name types without the global:: alias, which only generated code needs. A type in the global
    // namespace kept it, and the interface and handlers that SMD005 and SMD012 name in full kept it on every type.
    // Those stay in full, so two handlers of one simple name in different namespaces stay apart.
    public void DiagnosticMessages_NameTypesWithoutTheGlobalAlias()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;

            public sealed class Both : IRequest, IRequest<int>;

            public sealed class BothHandler : IRequestHandler<Both, int>
            {
                public ValueTask<int> Handle(Both message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }

            namespace Shop
            {
                public sealed class Dup : IRequest<int>;

                public sealed class DupHandlerA : IRequestHandler<Dup, int>
                {
                    public ValueTask<int> Handle(Dup message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                }

                public sealed class DupHandlerB : IRequestHandler<Dup, int>
                {
                    public ValueTask<int> Handle(Dup message, CancellationToken cancellationToken) => ValueTask.FromResult(2);
                }

                public sealed class EveryVoidRequest : IMiddleware<IRequest>
                {
                    public ValueTask Handle(IRequest message, MessageHandle<IRequest> next, CancellationToken cancellationToken) => next(message, cancellationToken);
                }
            }
            """;

        var run = RunGenerator(source);

        Assert.StartsWith("Request type 'Both' implements", Message("SMD002"), StringComparison.Ordinal);

        Assert.StartsWith(
            "Handler service 'Sharp.Mediator.IRequestHandler<Shop.Dup, int>' has multiple implementations: Shop.DupHandlerA, Shop.DupHandlerB.",
            Message("SMD005"), StringComparison.Ordinal);

        Assert.StartsWith(
            "Middleware 'EveryVoidRequest' implements 'Sharp.Mediator.IMiddleware<Sharp.Mediator.IRequest>', but 'IRequest'",
            Message("SMD012"), StringComparison.Ordinal);

        return;

        string Message(string id) => Assert.Single(run.Diagnostics, x => x.Id == id).GetMessage();
    }

    [Fact]
    // An event dispatches at its interface levels too, IEvent itself among them, so a middleware for IEvent runs for
    // every event; SMD012 once called it unreachable because IEvent is not among its own interfaces. A request
    // dispatches only at classes and structs, so a middleware for IRequest itself still never runs.
    public void ClosedMiddleware_ForIEventItself_ReportsNothing_ButForIRequestItself_ReportsSmd012()
    {
        const string source = ClosedMiddlewareFixture +
            """

            public sealed class EveryEvent : IMiddleware<IEvent>
            {
                public ValueTask Handle(IEvent message, MessageHandle<IEvent> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }

            public sealed class EveryVoidRequest : IMiddleware<IRequest>
            {
                public ValueTask Handle(IRequest message, MessageHandle<IRequest> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        var diagnostic = Assert.Single(run.Diagnostics, static x => x.Id == "SMD012");
        Assert.Contains("EveryVoidRequest", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    // ---- handlers AddHandlers cannot see ----------------------------------------------------------------------------

    [Fact]
    public void ConcreteGenericHandler_ReportsSmd006()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Box<T>(T Value) : IRequest<T>;

            public partial class BoxHandler<T> : IRequestHandler<Box<T>, T>
            {
                public ValueTask<T> Handle(Box<T> message, CancellationToken cancellationToken) => ValueTask.FromResult(message.Value);
            }

            public partial class BoxHandler<T> : System.IDisposable
            {
                public void Dispose() { }
            }

            public sealed class Unrelated<T> : System.IDisposable
            {
                public void Dispose() { }
            }

            public abstract class AbstractBase<T> : IRequestHandler<Box<T>, T>
            {
                public abstract ValueTask<T> Handle(Box<T> message, CancellationToken cancellationToken);
            }

            public sealed class IntBoxHandler : BoxHandler<int>;
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        var diagnostic = Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD006"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("BoxHandler", diagnostic.GetMessage());
        Assert.Contains("global::GeneratorFixture.IntBoxHandler", RegistrationsText(run));
    }

    [Fact]
    public void PrivateHandlerOfAVisibleMessage_ReportsSmd007_Once()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Visible : IRequest<int>;

            public static class Container
            {
                private sealed partial class Hidden : IRequestHandler<Visible, int>
                {
                    public ValueTask<int> Handle(Visible message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                }

                private sealed partial class Hidden : System.IDisposable
                {
                    public void Dispose() { }
                }
            }
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        var diagnostic = Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD007"));
        Assert.Contains("Hidden", diagnostic.GetMessage());
    }

    [Fact]
    public void PartialHandlerOfAnInaccessibleMessage_ReportsSmd003_Once()
    {
        // The syntax provider visits every declaration of a partial class.
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public static class Container
            {
                private sealed record Hidden : IRequest<int>;

                private sealed partial class HiddenHandler : IRequestHandler<Hidden, int>
                {
                    public ValueTask<int> Handle(Hidden message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                }

                private sealed partial class HiddenHandler : System.IDisposable
                {
                    public void Dispose() { }
                }
            }
            """;

        var run = RunGenerator(source);
        Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD003"));
    }

    [Fact]
    // A protected or private protected type is as far out of generated code's reach as a private one, and so is a
    // public type nested in a private one. The messages name those cases too.
    public void ProtectedAndNestedTypes_AreReportedAsInaccessible()
    {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Visible : IRequest<int>;
            public sealed record Reachable : IRequest<int>;

            public class Container
            {
                protected sealed record Guarded : IRequest<int>;

                protected sealed class GuardedHandler : IRequestHandler<Guarded, int>
                {
                    public ValueTask<int> Handle(Guarded message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                }

                private protected sealed class VisibleHandler : IRequestHandler<Visible, int>
                {
                    public ValueTask<int> Handle(Visible message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                }

                private static class Inner
                {
                    public sealed class ReachableHandler : IRequestHandler<Reachable, int>
                    {
                        public ValueTask<int> Handle(Reachable message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                    }
                }
            }
            """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        var message = Assert.Single(run.Diagnostics.Where(static x => x.Id == "SMD003")).GetMessage();
        Assert.Contains("Guarded", message);
        Assert.Contains("private, protected, or file-local, or nested in such a type", message);

        var handlers = run.Diagnostics.Where(static x => x.Id == "SMD007").Select(static x => x.GetMessage()).ToArray();
        Assert.Equal(2, handlers.Length);
        Assert.Contains(handlers, static x => x.Contains("VisibleHandler"));
        Assert.Contains(handlers, static x => x.Contains("ReachableHandler"));
        Assert.All(handlers, static x => Assert.Contains("private, protected, or file-local, or nested in such a type", x));
    }

    [Theory]
    [InlineData(LanguageVersion.CSharp8)]
    [InlineData(LanguageVersion.CSharp7_3)]
    // Generated code opens with #nullable and runs a module initializer, which C# 9 brought, so a project that set an
    // older LangVersion got compile errors inside generated files that did not name the cause. It now gets no generated
    // code and one error that names the setting.
    public void LanguageVersionBelow9_EmitsNothing_AndReportsSmd020(LanguageVersion version)
    {
        var run = RunGenerator(CSharp8Fixture, parseOptions: new CSharpParseOptions(version));
        Assert.Empty(Assert.Single(run.Result.Results).GeneratedSources);
        var diagnostic = Assert.Single(run.Result.Diagnostics);
        Assert.Equal("SMD020", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains($"C# {version.ToDisplayString()}", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    // C# 9 is enough: the generated registrations compile, without interceptors, which need C# 11.
    public void LanguageVersion9_GetsGeneratedCodeThatCompiles()
    {
        var run = RunGenerator(CSharp8Fixture, parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9));
        AssertCompiles(run);
        Assert.Empty(run.Result.Diagnostics);
        Assert.Contains("GeneratedMediatorRegistrar", RegistrationsText(run));
    }

    [Fact]
    // A compilation that sees the generator without referencing the runtime gets nothing generated at any version, so
    // it gets no error about the version either.
    public void LanguageVersionBelow9_WithoutTheRuntime_ReportsNothing()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp8);
        var compilation = CSharpCompilation.Create(
            "NoRuntimeReference",
            [CSharpSyntaxTree.ParseText("public sealed class Plain { }", parseOptions, "Plain.cs")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = CSharpGeneratorDriver
            .Create([new MediatorGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGenerators(compilation)
            .GetRunResult();

        Assert.Empty(result.Diagnostics);
        Assert.Empty(Assert.Single(result.Results).GeneratedSources);
    }
}