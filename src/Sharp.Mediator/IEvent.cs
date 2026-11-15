namespace Sharp.Mediator;

/// <summary>An event dispatched to zero or more handlers; semantics are determined by the configured <see cref="IEventPublisher" />.</summary>
public interface IEvent : IMessage;