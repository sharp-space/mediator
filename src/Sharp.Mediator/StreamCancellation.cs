using System.Runtime.CompilerServices;

namespace Sharp.Mediator;

/// <summary>
/// Gives the enumerator of a handler's stream the token the handler was called with. A consumer cancels a stream through the token of the call or the
/// token it enumerates with, and a stream may observe only the token its own enumerator receives, as EF Core's <c>AsAsyncEnumerable</c> does.
/// </summary>
internal static class StreamCancellation
{
    /// <summary>
    /// Wraps a stream-request handler with its stream-middleware chain, the first registered outermost. Without middleware the chain is the handler's own
    /// method, whose stream the caller passes to
    /// <see cref="Pass{TRequest, TResponse}(StreamMessageHandle{TRequest, TResponse}, IAsyncEnumerable{TResponse}, CancellationToken)" />; with middleware
    /// the chain's end wraps the handler's stream, with the token the last middleware passes on.
    /// </summary>
    public static StreamMessageHandle<TRequest, TResponse> Chain<TRequest, TResponse>(
        IStreamRequestHandler<TRequest, TResponse> handler,
        IStreamMiddleware<TRequest, TResponse>[] middlewares
    ) where TRequest : IStreamRequest<TResponse>
    {
        if (middlewares.Length == 0) return handler.Handle;

        StreamMessageHandle<TRequest, TResponse> chain = new End<TRequest, TResponse>(handler.Handle).Handle;
        for (var i = middlewares.Length - 1; i >= 0; i--) chain = new Step<TRequest, TResponse>(middlewares[i], chain).Handle;

        return chain;
    }

    /// <summary>
    /// Passes <paramref name="cancellationToken" /> on to the enumerator of the stream that <paramref name="chain" />, from
    /// <see cref="Chain{TRequest, TResponse}" />, returned, when the token can be canceled.
    /// </summary>
    // Each call site calls its chain once and hands the stream over, so the JIT profiles one delegate call per site
    // whichever tokens the calls pass: an interceptor's call of its message's handler, as before the token was passed
    // on. A call in each branch of the test would leave the branch that the warm-up never took without a profile.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IAsyncEnumerable<TResponse> Pass<TRequest, TResponse>(
        StreamMessageHandle<TRequest, TResponse> chain,
        IAsyncEnumerable<TResponse> stream,
        CancellationToken cancellationToken
    ) where TRequest : IStreamRequest<TResponse> =>
        cancellationToken.CanBeCanceled ? Wrap(chain, stream, cancellationToken) : stream;

    /// <summary>Wraps <paramref name="stream" /> when <paramref name="cancellationToken" /> can be canceled.</summary>
    public static IAsyncEnumerable<TResponse> Pass<TResponse>(IAsyncEnumerable<TResponse> stream, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? new CallTokenStream<TResponse>(stream, cancellationToken) : stream;

    // Out of line, so that the test of the token adds only a jump to the code that inlines a call site.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IAsyncEnumerable<TResponse> Wrap<TRequest, TResponse>(
        StreamMessageHandle<TRequest, TResponse> chain,
        IAsyncEnumerable<TResponse> stream,
        CancellationToken cancellationToken
    ) where TRequest : IStreamRequest<TResponse> =>
        // A chain with middleware starts with a Step, and its End has wrapped the handler's stream already: what
        // returns here is a middleware's stream, which honors the token itself. Any other chain is the handler's own
        // method. A test of the exact Step type costs a comparison, where one of the handler's interface calls a
        // cast helper.
        chain.Target is Step<TRequest, TResponse> ? stream : new CallTokenStream<TResponse>(stream, cancellationToken);

    private sealed class Step<TRequest, TResponse>(
        IStreamMiddleware<TRequest, TResponse> middleware,
        StreamMessageHandle<TRequest, TResponse> next
    ) where TRequest : IStreamRequest<TResponse>
    {
        public IAsyncEnumerable<TResponse> Handle(TRequest message, CancellationToken cancellationToken) =>
            middleware.Handle(message, next, cancellationToken);
    }

    // Holds the handler's method as a delegate, as the chain's end did before it passed the token on: an interface call
    // here, in code shared by every message, would go through a guess of the JIT for all of their handlers.
    private sealed class End<TRequest, TResponse>(StreamMessageHandle<TRequest, TResponse> handle)
        where TRequest : IStreamRequest<TResponse>
    {
        public IAsyncEnumerable<TResponse> Handle(TRequest message, CancellationToken cancellationToken) =>
            Pass(handle(message, cancellationToken), cancellationToken);
    }

    private sealed class CallTokenStream<T>(IAsyncEnumerable<T> source, CancellationToken callToken) : IAsyncEnumerable<T>
    {
        // No token of its own, or the call's, hands out the source's own enumerator, which then costs nothing per item.
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (!cancellationToken.CanBeCanceled || cancellationToken == callToken) return source.GetAsyncEnumerator(callToken);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(callToken, cancellationToken);

            try
            {
                return new LinkedEnumerator<T>(source.GetAsyncEnumerator(linked.Token), linked);
            }
            catch
            {
                linked.Dispose();
                throw;
            }
        }
    }

    private sealed class LinkedEnumerator<T>(IAsyncEnumerator<T> source, CancellationTokenSource linked) : IAsyncEnumerator<T>
    {
        public T Current => source.Current;

        public ValueTask<bool> MoveNextAsync() => source.MoveNextAsync();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                linked.Dispose();
            }
        }
    }
}