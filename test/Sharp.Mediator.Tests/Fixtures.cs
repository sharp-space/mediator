using System.Runtime.CompilerServices;
using Sharp.Mediator.Fixtures;

namespace Sharp.Mediator.Tests;

public sealed class Counter
{
    private int _value;

    public int Value => Volatile.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);
}

public sealed record VoidRequest : IRequest;

public sealed record EchoRequest(string Message) : IRequest<string>;

public sealed record ThrowingRequest : IRequest<string>;

public sealed class VoidRequestHandler(Counter counter) : IRequestHandler<VoidRequest>
{
    public ValueTask Handle(VoidRequest message, CancellationToken cancellationToken)
    {
        counter.Increment();
        return ValueTask.CompletedTask;
    }
}

public sealed class EchoRequestHandler : IRequestHandler<EchoRequest, string>
{
    public ValueTask<string> Handle(EchoRequest message, CancellationToken cancellationToken) => ValueTask.FromResult($"echo:{message.Message}");
}

public sealed class ThrowingRequestHandler : IRequestHandler<ThrowingRequest, string>
{
    public ValueTask<string> Handle(ThrowingRequest message, CancellationToken cancellationToken) =>
        ValueTask.FromException<string>(new InvalidOperationException("boom"));
}

public sealed record TestEvent(string Tag) : IEvent;

public sealed class FirstEventHandler(TraceLog trace) : IEventHandler<TestEvent>
{
    public ValueTask Handle(TestEvent message, CancellationToken cancellationToken)
    {
        trace.Add($"first:{message.Tag}");
        return ValueTask.CompletedTask;
    }
}

public sealed class SecondEventHandler(TraceLog trace) : IEventHandler<TestEvent>
{
    public ValueTask Handle(TestEvent message, CancellationToken cancellationToken)
    {
        trace.Add($"second:{message.Tag}");
        return ValueTask.CompletedTask;
    }
}

// Genuinely-async handlers (suspend via Task.Yield) so the object/box dispatch hits its await paths rather than the
// synchronously-completed fast path.
public sealed record AsyncEchoRequest(string Message) : IRequest<string>;

public sealed class AsyncEchoRequestHandler : IRequestHandler<AsyncEchoRequest, string>
{
    public async ValueTask<string> Handle(AsyncEchoRequest message, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return $"async:{message.Message}";
    }
}

public sealed record AsyncVoidRequest : IRequest;

public sealed class AsyncVoidRequestHandler(Counter counter) : IRequestHandler<AsyncVoidRequest>
{
    public async ValueTask Handle(AsyncVoidRequest message, CancellationToken cancellationToken)
    {
        await Task.Yield();
        counter.Increment();
    }
}

// Event with an async-completing handler, used to drive ParallelPublishing's Task.WhenAll/AsTask branch (count >= 2
// where a handler suspends). The async handler is registered first so it lands at chains[0], making the inner AsTask
// loop run over the trailing sync chain.
public sealed record ParallelEvent(string Tag) : IEvent;

public sealed class ParallelAsyncEventHandler(TraceLog trace) : IEventHandler<ParallelEvent>
{
    public async ValueTask Handle(ParallelEvent message, CancellationToken cancellationToken)
    {
        await Task.Yield();
        trace.Add($"async:{message.Tag}");
    }
}

public sealed class ParallelSyncEventHandler(TraceLog trace) : IEventHandler<ParallelEvent>
{
    public ValueTask Handle(ParallelEvent message, CancellationToken cancellationToken)
    {
        trace.Add($"sync:{message.Tag}");
        return ValueTask.CompletedTask;
    }
}

public sealed record StreamRequest(int Count) : IStreamRequest<int>;

public sealed class StreamRequestHandler : IStreamRequestHandler<StreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(StreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return i;
            await Task.Yield();
        }
    }
}