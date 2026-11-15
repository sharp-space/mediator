namespace Sharp.Mediator.Publishers;

/// <summary>
/// Invokes handler chains concurrently, but allocates nothing when they all complete synchronously: each chain is started in turn, and as soon as one
/// does not complete synchronously the remaining chains are started and the pending ones are awaited together via <see cref="Task.WhenAll(Task[])" />
/// (so genuinely-async handlers still run concurrently). A <see cref="ValueTask" /> cannot go into <c>WhenAll</c> directly, so the not-yet-completed
/// ones are materialized with <see cref="ValueTask.AsTask" />. Every chain runs even when another fails, however the failure was raised. Awaiting the
/// result rethrows a single failure as it is, and throws two or more together in an <see cref="AggregateException" />. Handlers of one event share a DI
/// scope, so only choose this when they do not share scoped state.
/// </summary>
public sealed class ParallelPublisher : IEventPublisher
{
    /// <inheritdoc />
    public ValueTask Publish(
        IReadOnlyList<EventHandlerExecutor> executors,
        IEvent message,
        CancellationToken cancellationToken
    ) => ParallelPublishing.Publish(executors, message, cancellationToken);
}

/// <summary>
/// Single shared implementation of the parallel event-dispatch loop: sync-aware (zero allocation when every chain completes synchronously) and
/// concurrent for genuinely-async chains. Used by <see cref="ParallelPublisher" /> over executors, which may span every level of an event, and by the
/// mediator over the typed chains of one level.
/// </summary>
/// <remarks>
/// A <see cref="ValueTask" /> cannot go into <see cref="Task.WhenAll(Task[])" /> directly, so once a chain does not complete synchronously the remaining
/// chains are started and the pending ones are materialized with <see cref="ValueTask.AsTask" /> and awaited together.
/// </remarks>
internal static class ParallelPublishing
{
    /// <summary>Invokes every chain concurrently; allocates nothing when all complete synchronously.</summary>
    public static ValueTask Publish<TEvent>(
        MessageHandle<TEvent>[] chains,
        TEvent message,
        CancellationToken cancellationToken
    ) where TEvent : IEvent
    {
        var count = chains.Length;
        switch (count)
        {
            case 0: return default;
            case 1: return chains[0](message, cancellationToken);
        }

        for (var i = 0; i < count; i++)
        {
            var task = Invoke(chains[i], message, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                task.GetAwaiter().GetResult();
                continue;
            }

            // First chain that didn't complete successfully: start the rest, then await them all together.
            // A faulted ValueTask is not IsCompletedSuccessfully, so AsTask() surfaces its fault.
            var pending = new Task[count - i];
            pending[0] = task.AsTask();
            for (var j = i + 1; j < count; j++)
                pending[j - i] = Invoke(chains[j], message, cancellationToken).AsTask();

            return new ValueTask(Join(pending));
        }

        return default; // all completed synchronously → zero allocation
    }

    /// <summary>The same loop over executors.</summary>
    public static ValueTask Publish(
        IReadOnlyList<EventHandlerExecutor> executors,
        IEvent message,
        CancellationToken cancellationToken
    )
    {
        var count = executors.Count;
        switch (count)
        {
            case 0: return default;
            case 1: return executors[0].Invoke(message, cancellationToken);
        }

        for (var i = 0; i < count; i++)
        {
            var task = Invoke(executors[i], message, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                task.GetAwaiter().GetResult();
                continue;
            }

            var pending = new Task[count - i];
            pending[0] = task.AsTask();
            for (var j = i + 1; j < count; j++)
                pending[j - i] = Invoke(executors[j], message, cancellationToken).AsTask();

            return new ValueTask(Join(pending));
        }

        return default;
    }

    // Awaiting Task.WhenAll rethrows only its first failure, which would lose the others: the handlers of one event
    // are independent, and each failure needs reporting. One failure is still rethrown as it is, so a caller can
    // catch its type.
    private static async Task Join(Task[] pending)
    {
        var all = Task.WhenAll(pending);

        try
        {
            await all.ConfigureAwait(false);
        }
        catch when (all.Exception is { InnerExceptions.Count: > 1 })
        {
            // In chain order: WhenAll lists them in the order the chains completed.
            List<Exception> failures = [];
            foreach (var task in pending)
                if (task.Exception is { } exception)
                    failures.AddRange(exception.InnerExceptions);

            throw new AggregateException(failures);
        }
    }

    // A throwing chain must not depend on whether it threw or returned a faulted ValueTask: either way every other
    // chain still runs and joins, so which handlers execute cannot depend on how another one was written.
    private static ValueTask Invoke<TEvent>(
        MessageHandle<TEvent> chain,
        TEvent message,
        CancellationToken cancellationToken
    ) where TEvent : IEvent
    {
        try
        {
            return chain(message, cancellationToken);
        }
        catch (Exception exception)
        {
            return ValueTask.FromException(exception);
        }
    }

    private static ValueTask Invoke(EventHandlerExecutor executor, IEvent message, CancellationToken cancellationToken)
    {
        try
        {
            return executor.Invoke(message, cancellationToken);
        }
        catch (Exception exception)
        {
            return ValueTask.FromException(exception);
        }
    }
}