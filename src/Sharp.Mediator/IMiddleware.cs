namespace Sharp.Mediator;

/// <summary>Pipeline step for void requests and for events. Call <c>next</c> to continue, or omit it to short-circuit.</summary>
public interface IMiddleware<TMessage> where TMessage : IMessage
{
    /// <summary>Wraps the next stage of the pipeline.</summary>
    ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken);
}

/// <summary>Pipeline step for requests that return a response.</summary>
public interface IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
{
    /// <summary>Wraps the next stage of the pipeline and returns its response (or substitutes one).</summary>
    ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken);
}