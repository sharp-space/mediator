using System.Runtime.CompilerServices;

namespace Sharp.Mediator.Benchmarks.Fixtures;

public sealed record Ping(int Value) : IRequest<int>;

// Requests like Ping, so that a scope can spread its dispatches over distinct messages.
public sealed record Ping2(int Value) : IRequest<int>;

public sealed record Ping3(int Value) : IRequest<int>;

public sealed record Ping4(int Value) : IRequest<int>;

public sealed record Noop : IRequest;

public sealed record Count(int Value) : IStreamRequest<int>;

public sealed class PingHandler : IRequestHandler<Ping, int>
{
    public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(message.Value);
}

public sealed class Ping2Handler : IRequestHandler<Ping2, int>
{
    public ValueTask<int> Handle(Ping2 message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(message.Value);
}

public sealed class Ping3Handler : IRequestHandler<Ping3, int>
{
    public ValueTask<int> Handle(Ping3 message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(message.Value);
}

public sealed class Ping4Handler : IRequestHandler<Ping4, int>
{
    public ValueTask<int> Handle(Ping4 message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(message.Value);
}

public sealed class NoopHandler : IRequestHandler<Noop>
{
    public ValueTask Handle(Noop message, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class CountHandler : IStreamRequestHandler<Count, int>
{
    // ReSharper disable once AsyncMethodWithoutAwait — the iterator yields synchronously, so enumerating the stream
    // measures the mediator and the iterators, not the thread pool.
    public async IAsyncEnumerable<int> Handle(Count request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Value; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
        }
    }
}