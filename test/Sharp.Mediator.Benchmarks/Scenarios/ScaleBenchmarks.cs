using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.ScaleFixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// Dispatch in an application with many messages (1000 unless the MessageCount environment variable says otherwise). The first and last message
/// must cost the same, and neither may cost more than in the small application of the other scenarios: anything proportional to the message count
/// shows up here first.
/// </summary>
[MemoryDiagnoser]
public class ScaleBenchmarks
{
    private readonly FirstMessage _first = new();
    private readonly LastMessage _last = new();
    private IServiceProvider _scopedProvider = null!;
    private IMediator _singleton = null!;

    [GlobalSetup]
    public void Setup()
    {
        _singleton = Benchmarks.Setup.SharpScale(ServiceLifetime.Singleton).GetRequiredService<IMediator>();
        _scopedProvider = Benchmarks.Setup.SharpScale(ServiceLifetime.Scoped);
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Typed_First() => _singleton.Request<FirstMessage, int>(_first);

    [Benchmark]
    public ValueTask<int> Typed_Last() => _singleton.Request<LastMessage, int>(_last);

    [Benchmark]
    public ValueTask<int> Interface_First() => _singleton.Request(_first);

    [Benchmark]
    public ValueTask<int> Interface_Last() => _singleton.Request(_last);

    [Benchmark]
    public async ValueTask<int> RequestScope_First()
    {
        using var scope = _scopedProvider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Request<FirstMessage, int>(_first);
    }

    [Benchmark]
    public async ValueTask<int> RequestScope_Last()
    {
        using var scope = _scopedProvider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Request<LastMessage, int>(_last);
    }
}