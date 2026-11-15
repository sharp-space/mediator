using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Publishers;

// Direct unit tests over SequentialPublisher.Publish, which receives executors, and over the typed loops the mediator
// runs for one level of an event.
public sealed class SequentialPublisherTests
{
    private static readonly TestEvent Event = new("s");

    private static readonly IEventHandler<TestEvent> Unused = new StubHandler(static () => default);

    private static ValueTask Run(params MessageHandle<TestEvent>[] chains) =>
        new SequentialPublisher().Publish(
            EventHandlerExecutor<TestEvent>.Of([.. chains.Select(static _ => Unused)], chains), Event, CancellationToken.None);

    private static ValueTask RunChains(params MessageHandle<TestEvent>[] chains) =>
        SequentialPublishing.Publish(chains, Event, CancellationToken.None);

    private static ValueTask RunHandlers(params IEventHandler<TestEvent>[] handlers) =>
        SequentialPublishing.Publish(handlers, Event, CancellationToken.None);

    [Fact]
    public async Task Handlers_AllSyncCompleteWithoutSuspending_InOrder()
    {
        var log = new List<int>();

        IEventHandler<TestEvent> One(int n) => new StubHandler(() =>
        {
            log.Add(n);
            return ValueTask.CompletedTask;
        });

        var task = RunHandlers(One(1), One(2), One(3));
        Assert.True(task.IsCompletedSuccessfully, "all-synchronous handlers complete with zero allocation");

        await task;

        Assert.Equal([1, 2, 3], log);
    }

    [Fact]
    public async Task Handlers_Suspending_RunTheRestInOrder()
    {
        var log = new List<int>();

        IEventHandler<TestEvent> One(int n) => new StubHandler(() =>
        {
            log.Add(n);
            return ValueTask.CompletedTask;
        });

        // Held until the assertion below, so the handler is still pending however busy the machine is. A yield could
        // finish it on another thread before the loop looked, which then never reached the loop this test is for.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var suspending = new StubHandler(async () =>
        {
            await release.Task;
            log.Add(2);
        });

        var task = RunHandlers(One(1), suspending, One(3), One(4));
        Assert.False(task.IsCompleted, "a suspending handler forces the async tail");

        release.SetResult();
        await task;

        Assert.Equal([1, 2, 3, 4], log);
    }

    [Fact]
    public async Task Handlers_StopOnFirstException_LaterHandlerDoesNotRun()
    {
        var later = false;
        var failing = new StubHandler(static () => throw new InvalidOperationException("boom"));
        var next = new StubHandler(() =>
        {
            later = true;
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunHandlers(failing, next).AsTask());
        Assert.False(later);
    }

    [Fact]
    public async Task Handlers_StopOnFirstFailedSuspension_LaterHandlerDoesNotRun()
    {
        var later = false;

        // Held until the assertion below, so the handler fails after the loop has started waiting for it.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = new StubHandler(async () =>
        {
            await release.Task;
            throw new InvalidOperationException("boom");
        });

        var next = new StubHandler(() =>
        {
            later = true;
            return ValueTask.CompletedTask;
        });

        var task = RunHandlers(failing, next);
        Assert.False(task.IsCompleted, "a suspending handler forces the async tail");

        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(task.AsTask);
        Assert.False(later);
    }

    [Fact]
    public async Task TypedChains_MatchTheExecutorPath()
    {
        var log = new List<int>();

        MessageHandle<TestEvent> One(int n) => (_, _) =>
        {
            log.Add(n);
            return ValueTask.CompletedTask;
        };

        // Held until the assertion below, so the chain is still pending however busy the machine is.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask Suspending(TestEvent message, CancellationToken cancellationToken)
        {
            await release.Task;
            log.Add(2);
        }

        var allSync = RunChains(One(1));
        Assert.True(allSync.IsCompletedSuccessfully);

        await allSync;
        var task = RunChains(Suspending, One(3));
        Assert.False(task.IsCompleted, "a suspending chain forces the async tail");

        release.SetResult();
        await task;

        Assert.Equal([1, 2, 3], log);
    }

    [Fact]
    public async Task Count0_ReturnsDefault()
    {
        var task = Run();
        Assert.True(task.IsCompletedSuccessfully);

        await task;
    }

    [Fact]
    public async Task AllSync_ReturnsDefault_RunsInOrder()
    {
        var log = new List<int>();

        MessageHandle<TestEvent> One(int n) => (_, _) =>
        {
            log.Add(n);
            return ValueTask.CompletedTask;
        };

        var task = Run(One(1), One(2), One(3));
        Assert.True(task.IsCompletedSuccessfully, "all-synchronous chains complete with zero allocation");

        await task;

        Assert.Equal([1, 2, 3], log);
    }

    [Fact]
    public async Task AsyncChain_TakesAwaitRestPath_RunsRemainingInOrder()
    {
        var log = new List<int>();

        // Held until the assertion below, so the chain is still pending however busy the machine is.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask Async(TestEvent message, CancellationToken cancellationToken)
        {
            await release.Task;

            lock (log)
            {
                log.Add(1);
            }
        }

        ValueTask Sync(TestEvent message, CancellationToken cancellationToken)
        {
            lock (log)
            {
                log.Add(2);
            }

            return ValueTask.CompletedTask;
        }

        var task = Run(Async, Sync);
        Assert.False(task.IsCompletedSuccessfully, "a suspending chain forces the async tail");

        release.SetResult();
        await task;

        Assert.Equal([1, 2], log);
    }

    [Fact]
    public async Task StopsOnFirstException_LaterHandlerDoesNotRun()
    {
        var laterRan = 0;
        MessageHandle<TestEvent> faulted = (_, _) => ValueTask.FromException(new InvalidOperationException("boom"));
        MessageHandle<TestEvent> later = (_, _) =>
        {
            Interlocked.Increment(ref laterRan);
            return ValueTask.CompletedTask;
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(faulted, later).AsTask());
        Assert.Equal("boom", ex.Message);
        Assert.Equal(0, laterRan);
    }

    private sealed class StubHandler(Func<ValueTask> handle) : IEventHandler<TestEvent>
    {
        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken) => handle();
    }
}