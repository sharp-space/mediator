using System.Threading.Tasks.Sources;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;

namespace Sharp.Mediator.Tests.Dispatch;

// MediatorBox boxes the typed dispatch result for the object-typed path. The synchronously-completed fast path is
// covered by the object-dispatch integration tests; the genuinely-pending await branches are driven deterministically
// here (a TaskCompletionSource guarantees the task is incomplete when the box adapter inspects it).
public sealed class MediatorBoxTests
{
    [Fact]
    public async Task AsObject_CompletedTask_ReturnsBoxedValueSynchronously()
    {
        var vt = MediatorBox.AsObject(new ValueTask<string>("v"));
        Assert.True(vt.IsCompletedSuccessfully);
        Assert.Equal("v", await vt);
    }

    [Fact]
    public async Task AsObject_PendingTask_AwaitsThenBoxes()
    {
        var tcs = new TaskCompletionSource<string>();
        var vt = MediatorBox.AsObject(new ValueTask<string>(tcs.Task));
        Assert.False(vt.IsCompleted); // the pending await arm was taken

        tcs.SetResult("done");

        Assert.Equal("done", await vt);
    }

    [Fact]
    public async Task AsObjectVoid_CompletedTask_ReturnsNullSynchronously()
    {
        var vt = MediatorBox.AsObjectVoid(ValueTask.CompletedTask);
        Assert.True(vt.IsCompletedSuccessfully);
        Assert.Null(await vt);
    }

    [Fact]
    public async Task AsObjectVoid_CompletedValueTaskSource_IsConsumed()
    {
        var source = new CompletedValueTaskSource();
        var result = MediatorBox.AsObjectVoid(new ValueTask(source, 0));
        Assert.True(result.IsCompletedSuccessfully);
        Assert.True(source.GetResultCalled);
        Assert.Null(await result);
    }

    [Fact]
    public async Task AsObjectVoid_PendingTask_AwaitsThenReturnsNull()
    {
        var tcs = new TaskCompletionSource();
        var vt = MediatorBox.AsObjectVoid(new ValueTask(tcs.Task));
        Assert.False(vt.IsCompleted); // the pending await arm was taken

        tcs.SetResult();

        Assert.Null(await vt);
    }

    [Fact]
    public async Task AsObjectStream_ProjectsItems()
    {
        var items = new List<object?>();
        await foreach (var item in MediatorBox.AsObjectStream(Source(), CancellationToken.None))
            items.Add(item);

        Assert.Equal([1, 2, 3], items);

        return;

        static async IAsyncEnumerable<int> Source()
        {
            for (var i = 1; i <= 3; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }
    }

    [Fact]
    // Integration: object-typed dispatch of async handlers still routes through MediatorBox end-to-end.
    public async Task ObjectRequest_AsyncResponseHandler_RoundTrips()
    {
        var (mediator, _) = Build(x => x.AddHandler<AsyncEchoRequestHandler>());
        Assert.Equal("async:box", await mediator.Request((object)new AsyncEchoRequest("box")));
    }

    [Fact]
    public async Task ObjectRequest_AsyncVoidHandler_RoundTrips()
    {
        var (mediator, counter) = Build(x => x.AddHandler<AsyncVoidRequestHandler>());
        Assert.Null(await mediator.Request((object)new AsyncVoidRequest()));
        Assert.Equal(1, counter.Value);
    }

    private static (IMediator mediator, Counter counter) Build(Action<MediatorBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddMediator(configure);

        var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return (mediator, scope.ServiceProvider.GetRequiredService<Counter>());
    }

    private sealed class CompletedValueTaskSource : IValueTaskSource
    {
        public bool GetResultCalled { get; private set; }

        public void GetResult(short token) => GetResultCalled = true;

        public ValueTaskSourceStatus GetStatus(short token) => ValueTaskSourceStatus.Succeeded;

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags
        ) => throw new InvalidOperationException("A completed source must not register a continuation.");
    }
}