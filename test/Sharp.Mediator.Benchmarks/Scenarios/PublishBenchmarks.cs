using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>Event / notification publish with two registered handlers.</summary>
[MemoryDiagnoser]
public class PublishBenchmarks
{
    private readonly SignalEvent _sharpEvent = new();
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
    public ValueTask Sharp() => _sharp.Publish(_sharpEvent);

    [Benchmark]
    public ValueTask Sharp_Scoped() => _sharpScoped.Publish(_sharpEvent);
}