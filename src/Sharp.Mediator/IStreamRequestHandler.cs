namespace Sharp.Mediator;

/// <summary>Handles a <typeparamref name="TRequest" /> by yielding an asynchronous stream of <typeparamref name="TResponse" />.</summary>
public interface IStreamRequestHandler<in TRequest, out TResponse> where TRequest : IStreamRequest<TResponse>
{
    /// <summary>Produces the response stream.</summary>
    IAsyncEnumerable<TResponse> Handle(TRequest message, CancellationToken cancellationToken);
}