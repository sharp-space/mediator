namespace Sharp.Mediator.Benchmarks.Fixtures;

// A publisher of the application's own, which the mediator hands every level's handlers at once.
public sealed class InOrderPublisher : IEventPublisher
{
    public async ValueTask Publish(
        IReadOnlyList<EventHandlerExecutor> executors,
        IEvent message,
        CancellationToken cancellationToken
    )
    {
        for (var i = 0; i < executors.Count; i++)
            await executors[i].Invoke(message, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PassThroughMiddleware<TMessage, TResponse>
    : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandle<TMessage, TResponse> next,
        CancellationToken cancellationToken
    ) => next(message, cancellationToken);
}