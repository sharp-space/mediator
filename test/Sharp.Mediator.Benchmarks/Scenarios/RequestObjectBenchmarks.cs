using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>Request dispatch when the message type is only known at runtime as an object.</summary>
[MemoryDiagnoser]
public class RequestObjectBenchmarks
{
    private readonly object _sharpRequest = new Ping(42);
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
    public ValueTask<object?> Sharp() => _sharp.Request(_sharpRequest);

    [Benchmark]
    public ValueTask<object?> Sharp_Scoped() => _sharpScoped.Request(_sharpRequest);
}