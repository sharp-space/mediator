namespace Sharp.Mediator;

/// <summary>Marker for anything dispatched through the mediator (request, event, stream request).</summary>
public interface IMessage;

/// <summary>Marker for a message that produces a <typeparamref name="TResponse" />.</summary>
public interface IMessage<TResponse> : IMessage;