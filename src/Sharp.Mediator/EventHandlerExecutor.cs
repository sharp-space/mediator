namespace Sharp.Mediator;

/// <summary>
/// One handler a publish reaches, inside the middleware of the type it is declared for. An <see cref="IEventPublisher" /> receives one for each handler
/// and decides when each runs.
/// </summary>
public abstract class EventHandlerExecutor
{
    private protected EventHandlerExecutor()
    {
    }

    /// <summary>The handler instance.</summary>
    public abstract object Handler { get; }

    /// <summary>The type the handler is declared for: the event's own type, one of its base classes, or an interface it implements.</summary>
    public abstract Type EventType { get; }

    /// <summary>Runs the handler for <paramref name="message" />, inside its type's middleware.</summary>
    /// <exception cref="InvalidCastException"><paramref name="message" /> is not an <see cref="EventType" />.</exception>
    public abstract ValueTask Invoke(IEvent message, CancellationToken cancellationToken);
}

/// <summary>An executor for a handler declared for exactly <typeparamref name="TEvent" />.</summary>
internal sealed class EventHandlerExecutor<TEvent>(IEventHandler<TEvent> handler, MessageHandle<TEvent> chain)
    : EventHandlerExecutor where TEvent : IEvent
{
    public override object Handler => handler;

    public override Type EventType => typeof(TEvent);

    // A checked cast: a publisher can pass any event, and one of another type must fail here rather than reach the
    // handler as a TEvent.
    public override ValueTask Invoke(IEvent message, CancellationToken cancellationToken) =>
        chain((TEvent)message, cancellationToken);

    public static EventHandlerExecutor[] Of(IEventHandler<TEvent>[] handlers, MessageHandle<TEvent>[] chains)
    {
        var executors = new EventHandlerExecutor[handlers.Length];
        for (var i = 0; i < handlers.Length; i++) executors[i] = new EventHandlerExecutor<TEvent>(handlers[i], chains[i]);

        return executors;
    }
}