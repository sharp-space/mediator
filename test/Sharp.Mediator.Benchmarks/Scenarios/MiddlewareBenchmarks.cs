using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>Request dispatch with a single pass-through middleware in front of the handler.</summary>
[MemoryDiagnoser]
public class MiddlewareBenchmarks
{
    private readonly Ping _sharpRequest = new(42);
    private IMediator _sharp = null!;
    private IServiceScope _sharpScope = null!;
    private IMediator _sharpScoped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sharp = Benchmarks.Setup.Sharp(true).GetRequiredService<IMediator>();

        _sharpScope = Benchmarks.Setup.SharpScoped(true).CreateScope();
        _sharpScoped = _sharpScope.ServiceProvider.GetRequiredService<IMediator>();
    }

    [GlobalCleanup]
    public void Cleanup() => _sharpScope.Dispose();

    [Benchmark(Baseline = true)]
    public ValueTask<int> Sharp() => _sharp.Request<Ping, int>(_sharpRequest);

    [Benchmark]
    public ValueTask<int> Sharp_Scoped() => _sharpScoped.Request<Ping, int>(_sharpRequest);
}