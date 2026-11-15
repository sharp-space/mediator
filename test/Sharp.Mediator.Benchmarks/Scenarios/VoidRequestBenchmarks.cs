using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>Void request / command dispatch.</summary>
[MemoryDiagnoser]
public class VoidRequestBenchmarks
{
    private readonly Noop _sharpRequest = new();
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
    public void Cleanup() => _sharpScope.Dispose();

    [Benchmark(Baseline = true)]
    public ValueTask Sharp() => _sharp.Request(_sharpRequest);

    [Benchmark]
    public ValueTask Sharp_Scoped() => _sharpScoped.Request(_sharpRequest);
}