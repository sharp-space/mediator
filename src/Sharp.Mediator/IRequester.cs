namespace Sharp.Mediator;

/// <summary>Dispatches requests and stream requests. The part of <see cref="IMediator" /> for code that never publishes.</summary>
/// <remarks>
/// Every method throws <see cref="ArgumentNullException" /> for a null message, and <see cref="InvalidOperationException" /> when no handler is
/// registered for the message. The methods that take an <see cref="object" /> throw <see cref="ArgumentException" /> for an object of another kind, such
/// as one that is not a message, an event, or a message whose handlers are of another kind.
/// </remarks>
public interface IRequester
{
    /// <summary>Dispatches a void request to its handler.</summary>
    ValueTask Request<TRequest>(TRequest message, CancellationToken cancellationToken = default) where TRequest : IRequest;

    /// <summary>Dispatches a request and returns its response.</summary>
    ValueTask<TResponse> Request<TRequest, TResponse>(
        TRequest message,
        CancellationToken cancellationToken = default
    ) where TRequest : IRequest<TResponse>;

    /// <summary>Dispatches a request whose static type is only known by its <see cref="IRequest{TResponse}" /> interface.</summary>
    ValueTask<TResponse> Request<TResponse>(IRequest<TResponse> message, CancellationToken cancellationToken = default);

    /// <summary>Dispatches a request whose type is only known at runtime; returns the response boxed, or null for a void request.</summary>
    ValueTask<object?> Request(object message, CancellationToken cancellationToken = default);

    /// <summary>Dispatches a stream request and returns its response stream.</summary>
    IAsyncEnumerable<TResponse> RequestStream<TRequest, TResponse>(
        TRequest message,
        CancellationToken cancellationToken = default
    ) where TRequest : IStreamRequest<TResponse>;

    /// <summary>Dispatches a stream request whose static type is only known by its <see cref="IStreamRequest{TResponse}" /> interface.</summary>
    IAsyncEnumerable<TResponse> RequestStream<TResponse>(
        IStreamRequest<TResponse> message,
        CancellationToken cancellationToken = default
    );

    /// <summary>Dispatches a stream request whose type is only known at runtime.</summary>
    IAsyncEnumerable<object?> RequestStream(object message, CancellationToken cancellationToken = default);
}