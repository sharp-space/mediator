using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

// ReSharper disable OperatorIsCanBeUsed — the calls test exact types, which spares a null test; see the
// comment in MediatorCalls.
// ReSharper disable UnusedTypeParameter — TKey only makes each call an instance of one message's interceptors.
// ReSharper disable RedundantCast — a type-parameter message is compared through object, which the JIT folds into a
// method table comparison.
// ReSharper disable UnusedMember.Global — only the interceptors the generator emits call these methods.

namespace Sharp.Mediator.Bindings;

/// <summary>
/// Targets of generated call-site interceptors, one for each kind of call. An interceptor passes its message type and, as <c>TKey</c>, a struct that
/// only the interceptors of that message pass, which makes the call an instance of that message's own: the JIT profiles it per message and can inline
/// the handler behind it, where a call that every message shares would see them all at one site. A request or stream call invokes the chain the mediator
/// cached for its message type, and an event the mediator's publish of that key; any other call takes the uncached route out of line. A chain only
/// answers a message of exactly its type, since dispatch follows the runtime type, so <c>exact</c> says that the message is: an interceptor passes it
/// for a type nothing can derive from, whose instances always are, and the call then skips the runtime type check. Any other <see cref="IRequester" />
/// or <see cref="IPublisher" /> implementation, such as a decorator or a test double, receives the original call. What each call does is the library's:
/// generated code names only these methods, so dispatch can change without consumers being compiled again.
/// </summary>
/// <remarks>
/// Infrastructure for the Sharp.Mediator source generator. Not intended for direct use.
/// </remarks>
[StackTraceHidden]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MediatorCalls
{
    // The calls find the mediator by its exact type rather than with `is`. DefaultMediator is sealed, so the two agree
    // on every receiver but null, and reading the type of a null receiver throws the NullReferenceException that the
    // intercepted interface call throws. So the check needs no null test of its own, and the JIT sends each failed test
    // straight to the uncached route: two instructions fewer per cached call on arm64.
    //
    // A message typed by a type parameter is compared through object: called on the type parameter, GetType() is a
    // constrained call, and the JIT does not fold that into a method table comparison as it does for an object.
    //
    // The calls are inlined into their interceptors, where an exact that is a constant, or a flag the JIT reads as one,
    // folds away with the check it spares. The uncached route stays out of line, so that every call site stays small. A
    // null message goes on to it, which throws for one as the interface call would.
    //
    // For a DefaultMediator, an event's uncached route is its Publish, which publishes as PublishExact does. Inlined
    // beside PublishExact, its copy of the publish leaves the .NET 10 JIT making none of the interceptor's calls tail
    // calls, so the task of each takes a stack slot of its own, which every publish zeroes: 80 bytes more on x64.

    /// <summary>A void request.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask Request<TRequest, TKey>(
        IRequester requester,
        TRequest message,
        bool exact,
        CancellationToken cancellationToken
    ) where TRequest : IRequest where TKey : struct =>
        requester.GetType() == typeof(DefaultMediator) && message != null! &&
        (exact || ((object)message).GetType() == typeof(TRequest)) &&
        Unsafe.As<DefaultMediator>(requester).CachedChain<TRequest>() is { } chain
            ? chain(message, cancellationToken)
            : Uncached(requester, message!, cancellationToken);

    /// <summary>A request that names its type and its response.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask<TResponse> Request<TRequest, TResponse, TKey>(
        IRequester requester,
        TRequest message,
        bool exact,
        CancellationToken cancellationToken
    ) where TRequest : IRequest<TResponse> where TKey : struct =>
        requester.GetType() == typeof(DefaultMediator) && message != null! &&
        (exact || ((object)message).GetType() == typeof(TRequest)) &&
        Unsafe.As<DefaultMediator>(requester).CachedChain<TRequest, TResponse>() is { } chain
            ? chain(message, cancellationToken)
            : Uncached<TRequest, TResponse>(requester, message!, cancellationToken);

    /// <summary>
    /// A request whose response the call inferred from an argument of static type <typeparamref name="TRequest" />. An instance of a derived type takes the
    /// uncached route, which dispatches on the runtime type, since its own handler may differ.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask<TResponse> InferredRequest<TRequest, TResponse, TKey>(
        IRequester requester,
        IRequest<TResponse> message,
        bool exact,
        CancellationToken cancellationToken
    ) where TRequest : class, IRequest<TResponse> where TKey : struct =>
        requester.GetType() == typeof(DefaultMediator) && message != null! &&
        (exact || message.GetType() == typeof(TRequest)) &&
        Unsafe.As<DefaultMediator>(requester).CachedChain<TRequest, TResponse>() is { } chain
            ? chain(Unsafe.As<TRequest>(message), cancellationToken)
            : InferredRequest(requester, message!, cancellationToken);

    /// <summary>
    /// A request whose response the call inferred, on the uncached route, which dispatches on the runtime type: the call of an inferred request whose
    /// argument type generated code cannot write, and the route of one that has no chain.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<TResponse> InferredRequest<TResponse>(
        IRequester requester,
        IRequest<TResponse> message,
        CancellationToken cancellationToken
    ) => requester is DefaultMediator mediator
        ? mediator.Request(message, cancellationToken)
        : requester.Request(message, cancellationToken);

    /// <summary>A stream request that names its type and its response.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IAsyncEnumerable<TResponse> RequestStream<TRequest, TResponse, TKey>(
        IRequester requester,
        TRequest message,
        bool exact,
        CancellationToken cancellationToken
    ) where TRequest : IStreamRequest<TResponse> where TKey : struct =>
        requester.GetType() == typeof(DefaultMediator) && message != null! &&
        (exact || ((object)message).GetType() == typeof(TRequest)) &&
        Unsafe.As<DefaultMediator>(requester).CachedStreamChain<TRequest, TResponse>() is { } chain
            ? StreamCancellation.Pass(chain, chain(message, cancellationToken), cancellationToken)
            : UncachedStream<TRequest, TResponse>(requester, message!, cancellationToken);

    /// <summary>The stream counterpart of the keyed <c>InferredRequest</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IAsyncEnumerable<TResponse> InferredRequestStream<TRequest, TResponse, TKey>(
        IRequester requester,
        IStreamRequest<TResponse> message,
        bool exact,
        CancellationToken cancellationToken
    ) where TRequest : class, IStreamRequest<TResponse> where TKey : struct =>
        requester.GetType() == typeof(DefaultMediator) && message != null! &&
        (exact || message.GetType() == typeof(TRequest)) &&
        Unsafe.As<DefaultMediator>(requester).CachedStreamChain<TRequest, TResponse>() is { } chain
            ? StreamCancellation.Pass(chain, chain(Unsafe.As<TRequest>(message), cancellationToken), cancellationToken)
            : InferredRequestStream(requester, message!, cancellationToken);

    /// <summary>The stream counterpart of the uncached <c>InferredRequest</c>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IAsyncEnumerable<TResponse> InferredRequestStream<TResponse>(
        IRequester requester,
        IStreamRequest<TResponse> message,
        CancellationToken cancellationToken
    ) => requester is DefaultMediator mediator
        ? mediator.RequestStream(message, cancellationToken)
        : requester.RequestStream(message, cancellationToken);

    /// <summary>
    /// An event. An instance of exactly <typeparamref name="TEvent" /> takes the publish of <typeparamref name="TKey" />; a derived one, or null, the
    /// publish that finds what it is.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask Publish<TEvent, TKey>(
        IPublisher publisher,
        TEvent message,
        bool exact,
        CancellationToken cancellationToken
    ) where TEvent : IEvent where TKey : struct =>
        publisher.GetType() == typeof(DefaultMediator) && message != null! &&
        (exact || ((object)message).GetType() == typeof(TEvent))
            ? Unsafe.As<DefaultMediator>(publisher).PublishExact<TEvent, TKey>(message, cancellationToken)
            : UncachedPublish(publisher, message!, cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask Uncached<TRequest>(
        IRequester requester,
        TRequest message,
        CancellationToken cancellationToken
    ) where TRequest : IRequest => requester is DefaultMediator mediator
        ? mediator.Request(message, cancellationToken)
        : requester.Request(message, cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<TResponse> Uncached<TRequest, TResponse>(
        IRequester requester,
        TRequest message,
        CancellationToken cancellationToken
    ) where TRequest : IRequest<TResponse> =>
        requester is DefaultMediator mediator
            ? mediator.Request<TRequest, TResponse>(message, cancellationToken)
            : requester.Request<TRequest, TResponse>(message, cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IAsyncEnumerable<TResponse> UncachedStream<TRequest, TResponse>(
        IRequester requester,
        TRequest message,
        CancellationToken cancellationToken
    ) where TRequest : IStreamRequest<TResponse> =>
        requester is DefaultMediator mediator
            ? mediator.RequestStream<TRequest, TResponse>(message, cancellationToken)
            : requester.RequestStream<TRequest, TResponse>(message, cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask UncachedPublish<TEvent>(
        IPublisher publisher,
        TEvent message,
        CancellationToken cancellationToken
    ) where TEvent : IEvent => publisher is DefaultMediator mediator
        ? mediator.Publish(message, cancellationToken)
        : publisher.Publish(message, cancellationToken);
}