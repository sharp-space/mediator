using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Publishers;

// Direct unit tests over ParallelPublisher.Publish, which receives executors, and over the typed loop the mediator runs
// for one level of an event. The theories run both, since the result must not depend on which one a publish took.
public sealed class ParallelPublisherTests
{
    private static readonly TestEvent Event = new("p");

    private static readonly IEventHandler<TestEvent> Unused = new UnusedHandler();

    private static ValueTask Publish(bool typed, MessageHandle<TestEvent>[] chains) =>
        typed
            ? ParallelPublishing.Publish(chains, Event, CancellationToken.None)
            : new ParallelPublisher().Publish(
                EventHandlerExecutor<TestEvent>.Of([.. chains.Select(static _ => Unused)], chains), Event,
                CancellationToken.None);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Count0_ReturnsDefault_NothingRuns(bool typed)
    {
        var task = Publish(typed, []);
        Assert.True(task.IsCompletedSuccessfully);

        await task;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Count1_Passthrough_RunsSingleChain(bool typed)
    {
        var ran = 0;
        var task = Publish(typed, [
            (_, _) =>
            {
                Interlocked.Increment(ref ran);
                return ValueTask.CompletedTask;
            }
        ]);

        await task;

        Assert.Equal(1, ran);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManyAllSync_ReturnsDefault_AllRan(bool typed)
    {
        var ran = 0;
        MessageHandle<TestEvent> sync = (_, _) =>
        {
            Interlocked.Increment(ref ran);
            return ValueTask.CompletedTask;
        };

        var task = Publish(typed, [sync, sync, sync]);
        Assert.True(task.IsCompletedSuccessfully, "all-synchronous chains must complete with zero allocation");

        await task;

        Assert.Equal(3, ran);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedSyncAsyncSync_TakesWhenAllPath_RunsAllOnce(bool typed)
    {
        var first = 0;
        var middle = 0;
        var tail = 0;

        // Held until the assertion below, so the chain is still pending however busy the machine is.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // sync runs and is skipped from the pending set; async triggers WhenAll; the trailing sync chain still executes
        // (wrapped via AsTask) even though it completes synchronously.
        var task = Publish(typed, [SyncFirst, Async, SyncTail]);
        Assert.False(task.IsCompletedSuccessfully, "an async chain forces the WhenAll path");

        release.SetResult();
        await task;

        Assert.Equal(1, first);
        Assert.Equal(1, middle);
        Assert.Equal(1, tail);

        return;

        ValueTask SyncFirst(TestEvent message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref first);
            return ValueTask.CompletedTask;
        }

        async ValueTask Async(TestEvent message, CancellationToken cancellationToken)
        {
            await release.Task;
            Interlocked.Increment(ref middle);
        }

        ValueTask SyncTail(TestEvent message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref tail);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // Handlers of one event are independent, so every failure is reported, in chain order, and a chain that succeeded
    // between them adds nothing.
    public async Task TwoFailures_AreThrownTogether(bool typed)
    {
        MessageHandle<TestEvent> syncFaulted = (_, _) => ValueTask.FromException(new InvalidOperationException("sync-fault"));
        MessageHandle<TestEvent> succeeding = (_, _) => ValueTask.CompletedTask;
        MessageHandle<TestEvent> asyncFaulted = (_, cancellationToken) => new ValueTask(Task.Run(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("async-fault");
        }, cancellationToken));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => Publish(typed, [syncFaulted, succeeding, asyncFaulted]).AsTask());
        Assert.Equal(["sync-fault", "async-fault"], exception.InnerExceptions.Select(static x => x.Message));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // A single failure keeps its own type, so a caller can still catch it as such.
    public async Task OnePendingFailure_IsRethrownAsItIs(bool typed)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = Publish(typed, [PendingFault, Sync]).AsTask();
        pending.SetException(new InvalidOperationException("async-fault"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("async-fault", exception.Message);

        return;

        ValueTask Sync(TestEvent testEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        ValueTask PendingFault(TestEvent testEvent, CancellationToken cancellationToken) => new(pending.Task);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingThenSynchronousThrow_JoinsBeforeCompletionObservingBothFaults(bool typed)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Publish(typed, [PendingFault, SynchronousThrow]).AsTask();
        Assert.False(task.IsCompleted, "the synchronous throw must join the already-started pending chain");

        pending.SetException(new InvalidOperationException("async-fault"));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => task);
        Assert.Equal(["async-fault", "sync-throw"], exception.InnerExceptions.Select(static x => x.Message));

        return;

        ValueTask SynchronousThrow(TestEvent testEvent, CancellationToken cancellationToken) => throw new InvalidOperationException("sync-throw");

        ValueTask PendingFault(TestEvent testEvent, CancellationToken cancellationToken) => new(pending.Task);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SynchronousThrow_StillRunsEveryLaterChain(bool typed)
    {
        // Regression: `throw` skipped later handlers while an async method's fault did not, so which handlers ran
        // depended on how an unrelated handler was written.
        var laterRan = false;
        MessageHandle<TestEvent> synchronousThrow = (_, _) => throw new InvalidOperationException("sync-throw");
        MessageHandle<TestEvent> later = (_, _) =>
        {
            laterRan = true;
            return ValueTask.CompletedTask;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(typed, [synchronousThrow, later]).AsTask());
        Assert.True(laterRan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // Every chain starts before the publisher awaits any. Each chain here waits at a gate that the test opens only
    // after counting them, so the count shows the chains running together however busy the machine is: a loop that
    // awaited each chain before starting the next would have started only the first.
    public async Task StartsEveryChain_BeforeAwaitingAny(bool typed)
    {
        var started = new Counter();
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        MessageHandle<TestEvent> gated = (_, _) =>
        {
            started.Increment();
            return new ValueTask(open.Task);
        };

        var publish = Publish(typed, [gated, gated, gated]);

        Assert.Equal(3, started.Value);
        Assert.False(publish.IsCompleted, "every chain waits at the gate");

        open.SetResult();
        await publish;
    }

    private sealed class UnusedHandler : IEventHandler<TestEvent>
    {
        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken) => default;
    }
}