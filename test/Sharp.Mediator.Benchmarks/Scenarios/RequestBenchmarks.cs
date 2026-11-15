using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>Strongly typed request dispatch returning a value.</summary>
[MemoryDiagnoser]
public class RequestBenchmarks
{
    private readonly Ping _sharpRequest = new(42);
    private IMediator _sharp = null!;
    private IServiceScope _sharpScope = null!;
    private IMediator _sharpScoped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sharp = Benchmarks.Setup.Sharp().GetRequiredService<IMediator>();

        // Warm scope: resolve once, then dispatch within it — measures the per-scope cache hit (Scoped lifetime).
        _sharpScope = Benchmarks.Setup.SharpScoped().CreateScope();
        _sharpScoped = _sharpScope.ServiceProvider.GetRequiredService<IMediator>();
    }

    [GlobalCleanup]
    public void Cleanup() => _sharpScope.Dispose();

    [Benchmark(Baseline = true)]
    // The fully-typed generic path. (Without explicit type arguments, overload resolution binds
    // `Request(_sharpRequest)` to the interface-typed entry below: Ping : IRequest<int> does not satisfy the void
    // overload's IRequest constraint, and TResponse is not inferred for the two-generic overload.)
    public ValueTask<int> Sharp_Typed() => _sharp.Request<Ping, int>(_sharpRequest);

    [Benchmark]
    // The interface-typed entry (a route lookup by runtime type).
    public ValueTask<int> Sharp_Interface() => _sharp.Request(_sharpRequest);

    [Benchmark]
    public ValueTask<int> Sharp_Scoped_Typed() => _sharpScoped.Request<Ping, int>(_sharpRequest);
}