namespace Sharp.Mediator;

/// <summary>Handles a <typeparamref name="TEvent" />. A single class may implement this for multiple event types.</summary>
public interface IEventHandler<in TEvent> where TEvent : IEvent
{
    /// <summary>Processes the event.</summary>
    ValueTask Handle(TEvent message, CancellationToken cancellationToken);
}