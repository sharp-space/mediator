using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// A publish on a warm Singleton mediator by how many synchronous handlers its event has, and several events published in turn. A typed publish runs the
/// default publisher's loop in the interceptor of its call site, and through a publish that is an instance of that event's own, so the JIT guesses the
/// handlers behind the loop by the profile of that event alone: the handler when the event has one, and one of them when it has more. One, Two, Four and
/// Six publish an event of that many handlers again and again. Mixed publishes the four in turn, as an application that publishes several events does.
/// TwoAfterOne publishes the event of two handlers after the event of one has warmed up to tier 1, as Two does on its own: a profile that the events
/// shared would hold the first event's handler and counts.
/// </summary>
[MemoryDiagnoser]
public class EventHandlerCountBenchmarks
{
    private readonly Signal4 _four = new();

    private readonly Signal1 _one = new();
    private readonly Signal6 _six = new();
    private readonly Signal2 _two = new();
    private IMediator _mediator = null!;

    [GlobalSetup]
    public void Setup()
    {
        var provider = Benchmarks.Setup.SharpWith(builder => builder
            .AddHandler<Signal1Handler1>()
            .AddHandler<Signal2Handler1>()
            .AddHandler<Signal2Handler2>()
            .AddHandler<Signal4Handler1>()
            .AddHandler<Signal4Handler2>()
            .AddHandler<Signal4Handler3>()
            .AddHandler<Signal4Handler4>()
            .AddHandler<Signal6Handler1>()
            .AddHandler<Signal6Handler2>()
            .AddHandler<Signal6Handler3>()
            .AddHandler<Signal6Handler4>()
            .AddHandler<Signal6Handler5>()
            .AddHandler<Signal6Handler6>());

        // Each benchmark is named after the number of handlers its publish reaches, so that number has to hold. A
        // sealed record that is an IEvent has one other level a handler can be on, IEvent itself, which would take the
        // publish off the cached route of one level; the handlers count their calls in no other way, so that they do
        // nothing the benchmark would measure.
        Expect<Signal1>(provider, 1);
        Expect<Signal2>(provider, 2);
        Expect<Signal4>(provider, 4);
        Expect<Signal6>(provider, 6);
        Expect<IEvent>(provider, 0);
        _mediator = provider.GetRequiredService<IMediator>();
    }

    [Benchmark(Baseline = true)]
    public ValueTask One() => _mediator.Publish(_one);

    [Benchmark]
    public ValueTask Two() => _mediator.Publish(_two);

    [Benchmark]
    public ValueTask Four() => _mediator.Publish(_four);

    [Benchmark]
    public ValueTask Six() => _mediator.Publish(_six);

    /// <summary>Setup, then the event of one handler published until its publish has reached tier 1.</summary>
    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [GlobalSetup(Target = nameof(TwoAfterOne))]
    public void SetupAfterOne()
    {
        Setup();

        // Tiering promotes a method from a background thread, after a count of calls and a delay; a second is ample.
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 1000) _mediator.Publish(_one).GetAwaiter().GetResult();
    }

    [Benchmark]
    public ValueTask TwoAfterOne() => _mediator.Publish(_two);

    /// <summary>Each of the four events once, so a result is the mean of their publishes.</summary>
    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [Benchmark(OperationsPerInvoke = 4)]
    public void Mixed()
    {
        // Every handler completes synchronously, so each publish has completed when it returns.
        _mediator.Publish(_one).GetAwaiter().GetResult();
        _mediator.Publish(_two).GetAwaiter().GetResult();
        _mediator.Publish(_four).GetAwaiter().GetResult();
        _mediator.Publish(_six).GetAwaiter().GetResult();
    }

    private static void Expect<TEvent>(IServiceProvider provider, int count) where TEvent : IEvent
    {
        var found = provider.GetServices<IEventHandler<TEvent>>().Count();
        if (found != count)
            throw new InvalidOperationException($"{typeof(TEvent).Name} reaches {found} handlers, not {count}.");
    }
}