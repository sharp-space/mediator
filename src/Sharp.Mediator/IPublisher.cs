namespace Sharp.Mediator;

/// <summary>
/// Publishes events. The part of <see cref="IMediator" /> for code that only publishes. Application code calls this; <see cref="IEventPublisher" /> is
/// the strategy that decides how the handlers then run.
/// </summary>
/// <remarks>
/// Every method throws <see cref="ArgumentNullException" /> for a null message. An event without a handler publishes to none: the default
/// <see cref="IEventPublisher" /> does nothing, and any other receives the publish with no handlers. <see cref="Publish(object, CancellationToken)" />
/// throws <see cref="ArgumentException" /> for an object that is not an <see cref="IEvent" />.
/// </remarks>
public interface IPublisher
{
    /// <summary>Publishes an event using the configured default <see cref="IEventPublisher" />.</summary>
    ValueTask Publish<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : IEvent;

    /// <summary>
    /// Publishes an event using <paramref name="publisher" /> for this call only. Subsequent publishes on the same mediator are unaffected.
    /// </summary>
    ValueTask Publish<TEvent>(
        TEvent message,
        IEventPublisher? publisher,
        CancellationToken cancellationToken = default
    ) where TEvent : IEvent;

    /// <summary>Publishes an event whose type is only known at runtime.</summary>
    ValueTask Publish(object message, CancellationToken cancellationToken = default);
}