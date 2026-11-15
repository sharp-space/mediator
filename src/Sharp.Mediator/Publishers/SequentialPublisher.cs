namespace Sharp.Mediator.Publishers;

/// <summary>
/// Default publisher. Invokes event handler chains one after another, awaiting each before the next, and the first failure stops the rest. Handlers of
/// one event share a DI scope, so running them one at a time keeps scoped dependencies such as an EF Core <c>DbContext</c> safe. Allocates nothing when
/// every chain completes synchronously (the async tail is split off only when a chain actually suspends).
/// </summary>
public sealed class SequentialPublisher : IEventPublisher
{
    /// <inheritdoc />
    public ValueTask Publish(
        IReadOnlyList<EventHandlerExecutor> executors,
        IEvent message,
        CancellationToken cancellationToken
    ) => SequentialPublishing.Publish(executors, message, cancellationToken);
}

/// <summary>
/// Single shared implementation of the sequential event-dispatch loop: each chain runs after the previous one finished, and the first failure stops the
/// rest. Used by <see cref="SequentialPublisher" /> over executors, and by the mediator over the typed chains of one level, which needs no executors.
/// Allocates nothing when every chain completes synchronously; the async tail is split off only when a chain actually suspends.
/// </summary>
internal static class SequentialPublishing
{
    /// <summary>Invokes every chain in order; allocates nothing when all complete synchronously.</summary>
    public static ValueTask Publish<TEvent>(
        MessageHandle<TEvent>[] chains,
        TEvent message,
        CancellationToken cancellationToken
    ) where TEvent : IEvent
    {
        for (var i = 0; i < chains.Length; i++)
        {
            var task = chains[i](message, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                return AwaitRest(task, chains, i, message, cancellationToken);

            task.GetAwaiter().GetResult();
        }

        return default; // all completed synchronously → zero allocation
    }

    // The rest of a publish whose chain at startIndex suspended, here and in the loops of the typed entry points of
    // DefaultMediator, which run the default publisher inline.
    internal static async ValueTask AwaitRest<TEvent>(
        ValueTask first,
        MessageHandle<TEvent>[] chains,
        int startIndex,
        TEvent message,
        CancellationToken cancellationToken
    ) where TEvent : IEvent
    {
        await first.ConfigureAwait(false);

        for (var i = startIndex + 1; i < chains.Length; i++)
            await chains[i](message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same loop for handlers with no middleware, which need no delegates.</summary>
    public static ValueTask Publish<TEvent>(
        IEventHandler<TEvent>[] handlers,
        TEvent message,
        CancellationToken cancellationToken
    ) where TEvent : IEvent
    {
        for (var i = 0; i < handlers.Length; i++)
        {
            var task = handlers[i].Handle(message, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                return AwaitRest(task, handlers, i, message, cancellationToken);

            task.GetAwaiter().GetResult();
        }

        return default;
    }

    private static async ValueTask AwaitRest<TEvent>(
        ValueTask first,
        IEventHandler<TEvent>[] handlers,
        int startIndex,
        TEvent message,
        CancellationToken cancellationToken
    ) where TEvent : IEvent
    {
        await first.ConfigureAwait(false);

        for (var i = startIndex + 1; i < handlers.Length; i++)
            await handlers[i].Handle(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same loop over executors, which may span several levels of the event.</summary>
    public static ValueTask Publish(
        IReadOnlyList<EventHandlerExecutor> executors,
        IEvent message,
        CancellationToken cancellationToken
    )
    {
        var count = executors.Count;
        for (var i = 0; i < count; i++)
        {
            var task = executors[i].Invoke(message, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                return AwaitRest(task, executors, i, message, cancellationToken);

            task.GetAwaiter().GetResult();
        }

        return default;
    }

    private static async ValueTask AwaitRest(
        ValueTask first,
        IReadOnlyList<EventHandlerExecutor> executors,
        int startIndex,
        IEvent message,
        CancellationToken cancellationToken
    )
    {
        await first.ConfigureAwait(false);

        for (var i = startIndex + 1; i < executors.Count; i++)
            await executors[i].Invoke(message, cancellationToken).ConfigureAwait(false);
    }
}