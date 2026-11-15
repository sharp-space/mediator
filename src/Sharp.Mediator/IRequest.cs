namespace Sharp.Mediator;

/// <summary>A request dispatched to exactly one handler, returning no value.</summary>
public interface IRequest : IMessage;

/// <summary>A request dispatched to exactly one handler, returning a <typeparamref name="TResponse" />.</summary>
public interface IRequest<TResponse> : IMessage<TResponse>;