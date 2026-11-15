namespace Sharp.Mediator;

/// <summary>The "next" step inside a void-request or event middleware.</summary>
public delegate ValueTask MessageHandle<in TMessage>(
    TMessage message,
    CancellationToken cancellationToken
) where TMessage : IMessage;

/// <summary>The "next" step inside a request-with-response middleware.</summary>
public delegate ValueTask<TResponse> MessageHandle<in TMessage, TResponse>(
    TMessage message,
    CancellationToken cancellationToken
) where TMessage : IMessage<TResponse>;