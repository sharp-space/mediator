using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator.Tests.Dispatch;

public sealed class StreamRequestTests
{
    private static IMediator CreateMediator()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<StreamRequestHandler>());
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task TypedRequestStream_YieldsAllItems()
    {
        var mediator = CreateMediator();
        var items = new List<int>();
        await foreach (var item in mediator.RequestStream<StreamRequest, int>(new StreamRequest(5)))
            items.Add(item);

        Assert.Equal([0, 1, 2, 3, 4], items);
    }

    [Fact]
    public async Task InterfaceTypedRequestStream_YieldsAllItems()
    {
        var mediator = CreateMediator();
        IStreamRequest<int> request = new StreamRequest(3);
        var items = new List<int>();
        await foreach (var item in mediator.RequestStream(request))
            items.Add(item);

        Assert.Equal([0, 1, 2], items);
    }

    [Fact]
    public async Task ObjectRequestStream_YieldsBoxedItems()
    {
        var mediator = CreateMediator();

        var items = new List<object?>();
        await foreach (var item in mediator.RequestStream((object)new StreamRequest(2)))
            items.Add(item);

        Assert.Equal([0, 1], items);
    }

    [Fact]
    public async Task RequestStream_HonorsCancellation()
    {
        var mediator = CreateMediator();
        using var cancellationTokenSource = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            var count = 0;
            await foreach (var _ in mediator.RequestStream<StreamRequest, int>(new StreamRequest(100), cancellationTokenSource.Token))
                if (++count == 3)
                    await cancellationTokenSource.CancelAsync();
        });
    }

    [Fact]
    public void ObjectRequestStream_ThrowsForNonStreamRequest()
    {
        var mediator = CreateMediator();
        Assert.Throws<ArgumentException>(() => _ = mediator.RequestStream("not-a-stream-request"));
    }

    [Fact]
    // As for a request (see RequestTests), a stream request without a handler throws InvalidOperationException from
    // every entry point, and only an object that is not a message is an argument error.
    public void ObjectRequestStream_MessageWithoutAHandler_ThrowsInvalidOperationNamingIt()
    {
        var mediator = CreateMediator();
        var exception = Assert.Throws<InvalidOperationException>(() => _ = mediator.RequestStream((object)new RuntimeOrphanStream()));
        Assert.Contains(nameof(RuntimeOrphanStream), exception.Message);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("event")]
    [InlineData("void request")]
    [InlineData("request")]
    // As for Request(object) (see RequestTests), an object of another kind is an argument error: a request is told
    // apart by its interface when it returns nothing, and by the request routes otherwise.
    public void ObjectRequestStream_ObjectOfAnotherKind_ThrowsArgumentException(string kind)
    {
        var mediator = CreateMediator();
        var message = kind switch
        {
            "object" => new object(),
            "event" => new TestEvent("x"),
            "void request" => new VoidRequest(),
            _ => new EchoRequest("x")
        };

        var exception = Assert.Throws<ArgumentException>(() => _ = mediator.RequestStream(message));

        Assert.Contains(kind switch
        {
            "object" => "is not a message",
            "event" => "is an event with no stream handler",
            "void request" => "is a request with no stream handler",
            _ => "has a request handler and no stream handler"
        }, exception.Message);

        Assert.Contains("it takes a stream request", exception.Message);
    }

    [Fact]
    public void InterfaceTypedDerivedStream_WithDifferentResponseThanHandledBase_ThrowsDescriptiveUnknownHandler()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<BaseResponseStreamHandler>());
        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        IStreamRequest<string> message = new DerivedDifferentResponseStream();
        var exception = Assert.Throws<InvalidOperationException>(() => mediator.RequestStream(message));
        Assert.Contains("No handler is registered", exception.Message);
        Assert.Contains("IStreamRequest<System.String>", exception.Message);
    }

    // ---- abstractions as the static type ----------------------------------------------------------------------------

    [Fact]
    public async Task RequestStream_ThroughInterfaceTypeArgument_DispatchesByRuntimeType()
    {
        var mediator = CreateMediator();

        IStreamRequest<int> message = new StreamRequest(2);
        var items = new List<int>();
        await foreach (var item in mediator.RequestStream<IStreamRequest<int>, int>(message))
            items.Add(item);

        Assert.Equal([0, 1], items);
    }

    [Fact]
    public void RequestStream_ExactTypeWithoutHandler_Throws()
    {
        var mediator = CreateMediator();
        Assert.Throws<InvalidOperationException>(() => mediator.RequestStream<UnhandledStream, int>(new UnhandledStream()));
    }

    // ---- the token of the call reaches the enumerator of the handler's stream ---------------------------------------

    [Theory]
    [InlineData("typed", false, false)]
    [InlineData("typed", false, true)]
    [InlineData("typed", true, false)]
    [InlineData("typed", true, true)]
    [InlineData("interface", false, false)]
    [InlineData("interface", false, true)]
    [InlineData("interface", true, false)]
    [InlineData("interface", true, true)]
    [InlineData("object", false, false)]
    [InlineData("object", false, true)]
    [InlineData("object", true, false)]
    [InlineData("object", true, true)]
    // A consumer cancels a stream through the token of the call, and a stream may observe only the token its own
    // enumerator receives, as EF Core's AsAsyncEnumerable does. RequestStream used to pass the token to the handler
    // only as an argument, so such a stream kept producing items after the consumer canceled, whether it came back
    // directly or through a middleware that enumerates it without the token.
    public async Task RequestStream_HandlerStreamObservingOnlyItsEnumerator_IsCanceledThroughTheCallsToken(
        string entry,
        bool middleware,
        bool cached
    )
    {
        var mediator = new DefaultMediator(TokenProvider(middleware), cached ? 1 : 0);
        var request = new EnumeratorTokenRequest();
        using var cachingCancellation = new CancellationTokenSource();
        using var cancellation = new CancellationTokenSource();

        // The call that caches the chain passes the token on as well, as the calls that find it cached do.
        if (cached)
        {
            await AssertCanceledAfterFirstItem(
                mediator.RequestStream<EnumeratorTokenRequest, int>(request, cachingCancellation.Token), cachingCancellation);
        }

        switch (entry)
        {
            case "typed":
                await AssertCanceledAfterFirstItem(
                    mediator.RequestStream<EnumeratorTokenRequest, int>(request, cancellation.Token), cancellation);

                break;
            case "interface":
                await AssertCanceledAfterFirstItem(mediator.RequestStream(request, cancellation.Token), cancellation);

                break;
            default:
                await AssertCanceledAfterFirstItem(mediator.RequestStream((object)request, cancellation.Token), cancellation);

                break;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    // Through IMediator, the generated interceptors call the cached chain themselves; with them off, the mediator does.
    public async Task RequestStream_ThroughIMediator_IsCanceledThroughTheCallsToken(bool inferred, bool middleware)
    {
        IMediator mediator = new DefaultMediator(TokenProvider(middleware), 1);
        var request = new EnumeratorTokenRequest();
        _ = mediator.RequestStream<EnumeratorTokenRequest, int>(request);
        using var cancellation = new CancellationTokenSource();
        await AssertCanceledAfterFirstItem(
            inferred
                ? mediator.RequestStream(request, cancellation.Token)
                : mediator.RequestStream<EnumeratorTokenRequest, int>(request, cancellation.Token),
            cancellation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequestStream_EnumeratedWithAnotherToken_IsCanceledThroughEither(bool cancelCallToken)
    {
        var mediator = new DefaultMediator(TokenProvider(false), 1);
        using var callCancellation = new CancellationTokenSource();
        using var enumerationCancellation = new CancellationTokenSource();

        await using var items = mediator.RequestStream<EnumeratorTokenRequest, int>(new EnumeratorTokenRequest(), callCancellation.Token)
            .GetAsyncEnumerator(enumerationCancellation.Token);

        Assert.True(await items.MoveNextAsync());
        Assert.Equal(1, items.Current);

        await (cancelCallToken ? callCancellation : enumerationCancellation).CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await items.MoveNextAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // With no token of its own, or with the call's, the enumeration needs no linked source, so it gets the handler's
    // enumerator and pays nothing per item.
    public async Task RequestStream_EnumeratedWithoutAnotherToken_HandsOutTheHandlersEnumerator(bool withCallToken)
    {
        var mediator = new DefaultMediator(TokenProvider(false), 1);
        using var cancellation = new CancellationTokenSource();

        await using var items = mediator.RequestStream<EnumeratorTokenRequest, int>(new EnumeratorTokenRequest(), cancellation.Token)
            .GetAsyncEnumerator(withCallToken ? cancellation.Token : CancellationToken.None);

        Assert.IsType<EnumeratorTokenStream.Enumerator>(items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // A token that cannot be canceled has nothing to pass on, so the call adds nothing to the handler's stream.
    public void RequestStream_WithoutACancelableToken_ReturnsTheHandlersStream(bool cached)
    {
        var mediator = new DefaultMediator(TokenProvider(false), cached ? 1 : 0);
        var request = new EnumeratorTokenRequest();
        if (cached) _ = mediator.RequestStream<EnumeratorTokenRequest, int>(request);

        Assert.Same(EnumeratorTokenStream.Instance, mediator.RequestStream<EnumeratorTokenRequest, int>(request));
    }

    [Fact]
    public void RequestStream_EnumeratorOfAnotherTokenFailsToStart_ThrowsTheFailure()
    {
        var mediator = new DefaultMediator(TokenProvider(false), 1);
        using var callCancellation = new CancellationTokenSource();
        using var enumerationCancellation = new CancellationTokenSource();
        var stream = mediator.RequestStream<EnumeratorTokenRequest, int>(new EnumeratorTokenRequest(true), callCancellation.Token);
        var exception = Assert.Throws<InvalidOperationException>(() => stream.GetAsyncEnumerator(enumerationCancellation.Token));
        Assert.Equal(FailingStream.Failure, exception.Message);
    }

    private static IServiceProvider TokenProvider(bool middleware)
    {
        var services = new ServiceCollection();
        services.AddMediator(x =>
        {
            x.AddHandler<EnumeratorTokenHandler>();
            if (middleware) x.AddMiddleware<TokenlessForwardingMiddleware>();
        });

        return services.BuildServiceProvider();
    }

    private static async Task AssertCanceledAfterFirstItem<T>(IAsyncEnumerable<T> stream, CancellationTokenSource cancellation)
    {
        await using var items = stream.GetAsyncEnumerator();
        Assert.True(await items.MoveNextAsync());

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await items.MoveNextAsync());
    }

    public class BaseResponseStream : IStreamRequest<int>;

    private sealed class DerivedDifferentResponseStream : BaseResponseStream, IStreamRequest<string>;

    public sealed class BaseResponseStreamHandler : IStreamRequestHandler<BaseResponseStream, int>
    {
        public async IAsyncEnumerable<int> Handle(
            BaseResponseStream message,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            yield return 1;
        }
    }

    private sealed record UnhandledStream : IStreamRequest<int>;

    public sealed record EnumeratorTokenRequest(bool FailToEnumerate = false) : IStreamRequest<int>;

    // Ignores the token it is called with, as a handler that returns db.Orders.AsAsyncEnumerable() does.
    public sealed class EnumeratorTokenHandler : IStreamRequestHandler<EnumeratorTokenRequest, int>
    {
        public IAsyncEnumerable<int> Handle(EnumeratorTokenRequest message, CancellationToken cancellationToken) =>
            message.FailToEnumerate ? FailingStream.Instance : EnumeratorTokenStream.Instance;
    }

    // Enumerates the next stream without passing its token on, as a middleware may.
    internal sealed class TokenlessForwardingMiddleware : IStreamMiddleware<EnumeratorTokenRequest, int>
    {
        public async IAsyncEnumerable<int> Handle(
            EnumeratorTokenRequest message,
            StreamMessageHandle<EnumeratorTokenRequest, int> next,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await foreach (var item in next(message, cancellationToken))
                yield return item;
        }
    }

    // Endless, and canceled only through the token GetAsyncEnumerator receives.
    private sealed class EnumeratorTokenStream : IAsyncEnumerable<int>
    {
        public static readonly EnumeratorTokenStream Instance = new();

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(cancellationToken);

        public sealed class Enumerator(CancellationToken cancellationToken) : IAsyncEnumerator<int>
        {
            public int Current { get; private set; }

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                Current++;

                return ValueTask.FromResult(true);
            }

            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class FailingStream : IAsyncEnumerable<int>
    {
        public const string Failure = "The stream cannot be enumerated.";

        public static readonly FailingStream Instance = new();

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Failure);
    }
}