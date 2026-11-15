using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// A publish to two synchronous handlers on a warm Singleton mediator, by the levels of the event that the handlers are on and the publisher that runs
/// them. Own puts both on the event's own type. OwnAndBase puts one on its base class and OwnAndInterface one on an interface it implements, so that
/// the publish reaches two levels; OwnAndInterface_ValueType does the same with a value type, which a publish of two levels boxes. The default
/// publisher runs a level at a time, while ParallelPublisher and a publisher of the application's own receive every level's handlers in one array,
/// which a warm mediator keeps.
/// </summary>
[MemoryDiagnoser]
public class EventLevelBenchmarks
{
    /// <summary>A publisher the mediator can be configured with.</summary>
    public enum PublisherKind
    {
        // ReSharper disable once UnusedMember.Global — ParamsAllValues selects this value, and the default setup
        // branch uses it.
        Sequential,
        Parallel,
        Custom
    }

    private readonly SignalEvent _own = new();
    private readonly DerivedSignal _ownAndBase = new();
    private readonly TaggedSignal _ownAndInterface = new();
    private readonly ValueSignal _ownAndInterfaceValue = new();

    private IMediator _mediator = null!;

    /// <summary>The publisher the mediator is configured with.</summary>
    [ParamsAllValues]
    public PublisherKind Publisher { get; set; }

    [GlobalSetup]
    public void Setup() =>
        _mediator = Benchmarks.Setup.SharpWith(builder =>
            {
                builder
                    .AddHandler<FirstSignalHandler>()
                    .AddHandler<SecondSignalHandler>()
                    .AddHandler<DerivedSignalHandler>()
                    .AddHandler<BaseSignalHandler>()
                    .AddHandler<TaggedSignalHandler>()
                    .AddHandler<ValueSignalHandler>()
                    .AddHandler<TagHandler>();

                if (Publisher == PublisherKind.Parallel) builder.WithEventPublisher<ParallelPublisher>();
                else if (Publisher == PublisherKind.Custom) builder.WithEventPublisher<InOrderPublisher>();
            })
            .GetRequiredService<IMediator>();

    [Benchmark(Baseline = true)]
    public ValueTask Own() => _mediator.Publish(_own);

    [Benchmark]
    public ValueTask OwnAndBase() => _mediator.Publish(_ownAndBase);

    [Benchmark]
    public ValueTask OwnAndInterface() => _mediator.Publish(_ownAndInterface);

    [Benchmark]
    public ValueTask OwnAndInterface_ValueType() => _mediator.Publish(_ownAndInterfaceValue);
}