using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.ScaleFixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// A busy request scope in an application with many messages: a new scope that dispatches one message several times. Slots go to messages in the
/// order the process first dispatches them, so what the scope's cache allocates, and how its dispatches find their chain, can depend on what the
/// process has dispatched before. The first message holds a low slot either way, while the last message, in a process that has dispatched every
/// message, holds the highest.
/// </summary>
[MemoryDiagnoser]
public class ScopeCacheBenchmarks
{
    private readonly FirstMessage _first = new();
    private readonly LastMessage _last = new();
    private IServiceProvider _provider = null!;

    /// <summary>
    /// Whether the process dispatched every message before the measurement, the first one first and the last one last, so that they hold the first and the
    /// last slot. Otherwise the measured message holds the first slot.
    /// </summary>
    [Params(false, true)]
    public bool EveryMessageFirst { get; set; }

    /// <summary>
    /// The Scoped mediator caches at its eighth miss, so 8 dispatches build the scope's cache and find nothing in it, and 32 add 24 dispatches that find
    /// their chain there.
    /// </summary>
    [Params(8, 32)]
    public int Dispatches { get; set; }

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [GlobalSetup]
    public void Setup()
    {
        _provider = Benchmarks.Setup.SharpScale(ServiceLifetime.Scoped);

        if (!EveryMessageFirst) return;

        // Through the object entry point, which takes a message's slot when it dispatches the message: a typed call can
        // take it as early as when the method it is in is compiled.
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        foreach (var type in typeof(FirstMessage).Assembly.GetTypes()
                     .Where(static x => typeof(IRequest<int>).IsAssignableFrom(x))
                     .OrderBy(static x => x == typeof(FirstMessage) ? 0 : x == typeof(LastMessage) ? 2 : 1))
            _ = mediator.Request(Activator.CreateInstance(type)!).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<int> RequestScope_First()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = 0;
        for (var i = 0; i < Dispatches; i++)
            result = await mediator.Request<FirstMessage, int>(_first);

        return result;
    }

    [Benchmark]
    public async ValueTask<int> RequestScope_Last()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = 0;
        for (var i = 0; i < Dispatches; i++)
            result = await mediator.Request<LastMessage, int>(_last);

        return result;
    }
}