namespace Sharp.Mediator;

/// <summary>Handles a void <typeparamref name="TRequest" />.</summary>
public interface IRequestHandler<in TRequest> where TRequest : IRequest
{
    /// <summary>Processes the request.</summary>
    ValueTask Handle(TRequest message, CancellationToken cancellationToken);
}

/// <summary>Handles a <typeparamref name="TRequest" /> and returns a <typeparamref name="TResponse" />.</summary>
public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    /// <summary>Processes the request and returns its response.</summary>
    ValueTask<TResponse> Handle(TRequest message, CancellationToken cancellationToken);
}