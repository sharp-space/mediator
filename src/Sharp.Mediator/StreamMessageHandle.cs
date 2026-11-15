namespace Sharp.Mediator;

/// <summary>The "next" step inside a stream-request middleware.</summary>
public delegate IAsyncEnumerable<TResponse> StreamMessageHandle<in TMessage, out TResponse>(
    TMessage message,
    CancellationToken cancellationToken
) where TMessage : IStreamRequest<TResponse>;