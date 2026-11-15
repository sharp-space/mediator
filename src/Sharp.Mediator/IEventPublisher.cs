namespace Sharp.Mediator;

/// <summary>
/// Strategy for running the handlers of a publish (e.g. sequentially or in parallel). Implement to plug a custom dispatch strategy. Each executor
/// already wraps its handler in the middleware of the type it is declared for, so a publisher only has to invoke it.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Runs the handlers a publish of <paramref name="message" /> reaches, by invoking <paramref name="executors" />.</summary>
    /// <remarks>
    /// Called once per publish. <paramref name="executors" /> holds the handlers of every type the event is that declares them, in level order: its own
    /// type, its base classes from the nearest, then its interfaces by full name, and each type's handlers in registration order. It is empty when none of
    /// those types has a handler registered. A mediator that caches passes the same executors to every publish of the event. A value-type event is boxed
    /// for the call.
    /// </remarks>
    ValueTask Publish(IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken);
}