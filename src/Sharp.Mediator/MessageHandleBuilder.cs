namespace Sharp.Mediator;

/// <summary>
/// Composes a handler with a list of middleware into a single delegate. The middleware wraps the handler in registration order (first
/// registered is outermost).
/// </summary>
internal static class MessageHandleBuilder
{
    /// <summary>Wraps each event handler with the event's middleware chain.</summary>
    public static MessageHandle<TEvent>[] BuildEach<TEvent>(
        IEventHandler<TEvent>[] handlers,
        IMiddleware<TEvent>[] middlewares
    ) where TEvent : IEvent
    {
        var chains = new MessageHandle<TEvent>[handlers.Length];
        for (var i = 0; i < handlers.Length; i++)
            chains[i] = Build(handlers[i].Handle, middlewares);

        return chains;
    }

    /// <summary>Wraps a void-request / event handler with its middleware chain.</summary>
    public static MessageHandle<TMessage> Build<TMessage>(
        MessageHandle<TMessage> handle,
        IMiddleware<TMessage>[] middlewares
    ) where TMessage : IMessage
    {
        for (var i = middlewares.Length - 1; i >= 0; i--)
        {
            var middleware = middlewares[i];
            var next = handle;
            handle = (message, token) => middleware.Handle(message, next, token);
        }

        return handle;
    }

    /// <summary>Wraps a request-with-response handler with its middleware chain.</summary>
    public static MessageHandle<TMessage, TResponse> Build<TMessage, TResponse>(
        MessageHandle<TMessage, TResponse> handle,
        IMiddleware<TMessage, TResponse>[] middlewares
    ) where TMessage : IMessage<TResponse>
    {
        for (var i = middlewares.Length - 1; i >= 0; i--)
        {
            var middleware = middlewares[i];
            var next = handle;
            handle = (message, token) => middleware.Handle(message, next, token);
        }

        return handle;
    }
}