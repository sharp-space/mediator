using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// Stream request dispatch, then each part of enumerating the stream it returns. Sharp_Typed, Sharp_Interface, Sharp_Object, Sharp_Scoped_Typed and
/// Sharp_Typed_Cancelable call RequestStream and nothing else. The others enumerate a stream of <see cref="Items" /> items: to its first item, to its
/// end, disposing it after the first item, and canceling it after the first item. The handler yields synchronously, so enumeration measures the mediator
/// and the iterators rather than the thread pool.
/// </summary>
[MemoryDiagnoser]
public class StreamRequestBenchmarks
{
    /// <summary>How many items the stream yields.</summary>
    public const int Items = 10;

    private readonly CancellationTokenSource _cancelable = new();
    private readonly Count _sharpRequest = new(Items);

    private IMediator _sharp = null!;
    private IServiceScope _sharpScope = null!;
    private IMediator _sharpScoped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sharp = Benchmarks.Setup.Sharp().GetRequiredService<IMediator>();

        _sharpScope = Benchmarks.Setup.SharpScoped().CreateScope();
        _sharpScoped = _sharpScope.ServiceProvider.GetRequiredService<IMediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sharpScope.Dispose();
        _cancelable.Dispose();
    }

    [Benchmark(Baseline = true)]
    public IAsyncEnumerable<int> Sharp_Typed() => _sharp.RequestStream<Count, int>(_sharpRequest);

    [Benchmark]
    public IAsyncEnumerable<int> Sharp_Interface() => _sharp.RequestStream(_sharpRequest);

    [Benchmark]
    public IAsyncEnumerable<object?> Sharp_Object() => _sharp.RequestStream((object)_sharpRequest);

    [Benchmark]
    public IAsyncEnumerable<int> Sharp_Scoped_Typed() => _sharpScoped.RequestStream<Count, int>(_sharpRequest);

    [Benchmark]
    // A token that can be canceled, which the mediator passes on to the enumerator of the handler's stream.
    public IAsyncEnumerable<int> Sharp_Typed_Cancelable() => _sharp.RequestStream<Count, int>(_sharpRequest, _cancelable.Token);

    [Benchmark]
    // Leaves the enumerator undisposed so that only the time to the first item is measured: the handler holds nothing
    // that needs releasing, and Sharp_Typed_EarlyDispose adds the disposal.
    public async ValueTask<int> Sharp_Typed_FirstItem()
    {
        var items = _sharp.RequestStream<Count, int>(_sharpRequest).GetAsyncEnumerator();
        await items.MoveNextAsync();
        return items.Current;
    }

    [Benchmark]
    public async ValueTask<int> Sharp_Typed_AllItems()
    {
        var count = 0;
        await foreach (var _ in _sharp.RequestStream<Count, int>(_sharpRequest)) count++;

        return count;
    }

    [Benchmark]
    // Enumerated without a token of its own, so the handler's enumerator receives the call's.
    public async ValueTask<int> Sharp_Typed_Cancelable_AllItems()
    {
        var count = 0;
        await foreach (var _ in _sharp.RequestStream<Count, int>(_sharpRequest, _cancelable.Token)) count++;

        return count;
    }

    [Benchmark]
    // The object path boxes each item and yields it again from an iterator of its own.
    public async ValueTask<int> Sharp_Object_AllItems()
    {
        var count = 0;
        await foreach (var _ in _sharp.RequestStream((object)_sharpRequest)) count++;

        return count;
    }

    [Benchmark]
    // A caller that stops after the first item: disposal resumes the suspended iterator so it can finish.
    public async ValueTask<int> Sharp_Typed_EarlyDispose()
    {
        await using var items = _sharp.RequestStream<Count, int>(_sharpRequest).GetAsyncEnumerator();
        await items.MoveNextAsync();
        return items.Current;
    }

    [Benchmark]
    // The handler checks its token before each item, so the MoveNextAsync after the cancellation throws. A canceled
    // source cannot be reset, so each call creates its own.
    public async ValueTask<int> Sharp_Typed_Canceled()
    {
        using var source = new CancellationTokenSource();
        await using var items =
            _sharp.RequestStream<Count, int>(_sharpRequest, source.Token).GetAsyncEnumerator(source.Token);

        return await CancelAfterFirst(items, source);
    }

    [Benchmark]
    // The cancellation surfaces through the handler's iterator and then through the iterator that boxes its items.
    public async ValueTask<int> Sharp_Object_Canceled()
    {
        using var source = new CancellationTokenSource();
        await using var items = _sharp.RequestStream((object)_sharpRequest, source.Token).GetAsyncEnumerator(source.Token);
        return await CancelAfterFirst(items, source);
    }

    // Returns how many items arrived before the cancellation took effect, one, and throws if it never took effect, so
    // that --check fails for a scenario that no longer measures a cancellation.
    private static async ValueTask<int> CancelAfterFirst<T>(IAsyncEnumerator<T> items, CancellationTokenSource source)
    {
        await items.MoveNextAsync();
        await source.CancelAsync();

        try
        {
            await items.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            return 1;
        }

        throw new InvalidOperationException("The stream was not canceled.");
    }
}