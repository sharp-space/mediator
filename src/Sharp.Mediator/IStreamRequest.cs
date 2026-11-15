namespace Sharp.Mediator;

/// <summary>A request whose handler yields a stream of <typeparamref name="TResponse" /> items.</summary>
public interface IStreamRequest<TResponse> : IMessage<TResponse>;