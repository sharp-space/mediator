namespace Sharp.Mediator;

/// <summary>Pipeline step for stream requests. Forward the stream from <c>next</c>, or yield your own.</summary>
public interface IStreamMiddleware<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    /// <summary>Wraps the next stage of the stream pipeline.</summary>
    IAsyncEnumerable<TResponse> Handle(
        TRequest message,
        StreamMessageHandle<TRequest, TResponse> next,
        CancellationToken cancellationToken
    );
}