using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.ScaleFixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// The first dispatch in an application with many messages, once per process, after the module initializers have run and the provider is built:
/// StartupBenchmarks measures the initializers, and ScaleBenchmarks the same calls warm. A first dispatch looks up the message's route through generated
/// code that compares its type with the messages its assembly declares, one at a time, resolves the handler, and runs code that has not been compiled
/// yet. Typed_Last_AfterFirst comes after a typed dispatch of another message, which leaves what a message costs the first time it is dispatched in a
/// process that has dispatched before.
/// </summary>
[SimpleJob(RunStrategy.ColdStart, 15, iterationCount: 1)]
[MinColumn]
[MedianColumn]
public class FirstDispatchBenchmarks
{
    private readonly FirstMessage _first = new();
    private readonly LastMessage _last = new();
    private IMediator _mediator = null!;

    [GlobalSetup]
    public void Setup() =>
        _mediator = Benchmarks.Setup.SharpScale(ServiceLifetime.Singleton).GetRequiredService<IMediator>();

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [GlobalSetup(Target = nameof(Typed_Last_AfterFirst))]
    public void SetupAfterFirst()
    {
        Setup();
        _ = _mediator.Request<FirstMessage, int>(_first).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Typed_First() => _mediator.Request<FirstMessage, int>(_first);

    [Benchmark]
    public ValueTask<int> Typed_Last() => _mediator.Request<LastMessage, int>(_last);

    [Benchmark]
    public ValueTask<int> Interface_Last() => _mediator.Request(_last);

    [Benchmark]
    public ValueTask<object?> Object_Last() => _mediator.Request((object)_last);

    [Benchmark]
    public ValueTask<int> Typed_Last_AfterFirst() => _mediator.Request<LastMessage, int>(_last);
}