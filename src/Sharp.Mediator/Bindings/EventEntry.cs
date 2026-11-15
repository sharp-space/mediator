using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Bindings;

/// <summary>
/// Cached publish entry for one level, a declared event type: the handlers registered for exactly that type, their middleware chains (as arrays, to
/// avoid virtual indexers on the publish loop), and the default publisher. Stored in the owning <see cref="DefaultMediator" /> instance's slot array, so
/// its lifetime matches the mediator (and thus the DI scope).
/// </summary>
internal sealed class EventEntry<TEvent>(
    IEventHandler<TEvent>[] handlers,
    MessageHandle<TEvent>[] chains,
    IEventPublisher publisher
) where TEvent : IEvent
{
    /// <summary>The pre-built per-handler middleware chains.</summary>
    public readonly MessageHandle<TEvent>[] Chains = chains;

    /// <summary>The default publisher resolved with the handlers.</summary>
    public readonly IEventPublisher Publisher = publisher;

    /// <summary>The event handlers resolved for this event type.</summary>
    private readonly IEventHandler<TEvent>[] _handlers = handlers;

    /// <summary>What a publisher other than the shipped ones receives, built the first time one needs it.</summary>
    public EventHandlerExecutor[] Executors => field ??= EventHandlerExecutor<TEvent>.Of(_handlers, Chains);

    /// <summary>
    /// Invokes the chains through <paramref name="publisher" />. The shipped publishers run their loops over the arrays directly (no virtual publisher call,
    /// no executors), which does what they would do with the executors; any other publisher receives the executors.
    /// </summary>
    public ValueTask Publish(IEventPublisher publisher, TEvent message, CancellationToken cancellationToken) =>
        publisher switch
        {
            SequentialPublisher => SequentialPublishing.Publish(Chains, message, cancellationToken),
            ParallelPublisher => ParallelPublishing.Publish(Chains, message, cancellationToken),
            _ => publisher.Publish(Executors, message, cancellationToken)
        };

    /// <summary>The same, for the handlers and chains resolved for a single publish by a mediator that does not cache.</summary>
    public static ValueTask Publish(
        IEventPublisher publisher,
        IEventHandler<TEvent>[] handlers,
        MessageHandle<TEvent>[] chains,
        TEvent message,
        CancellationToken cancellationToken
    ) => publisher switch
    {
        SequentialPublisher => SequentialPublishing.Publish(chains, message, cancellationToken),
        ParallelPublisher => ParallelPublishing.Publish(chains, message, cancellationToken),
        _ => publisher.Publish(EventHandlerExecutor<TEvent>.Of(handlers, chains), message, cancellationToken)
    };
}