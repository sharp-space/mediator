using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// What a web request pays on the default Scoped lifetime: a new scope, the mediator resolved from it, and its dispatches. The other scenarios reuse
/// one warm scope, which no request does. Sharp dispatches one message, and Sharp_Distinct spreads the same dispatches over four, so a scope that
/// caches keeps four chains. Publish publishes an event of two handlers as often, such as the domain events of a request, whose misses resolve every
/// handler of the event.
/// </summary>
[MemoryDiagnoser]
public class RequestScopeBenchmarks
{
    private readonly SignalEvent _event = new();

    private readonly Ping _request = new(42);
    private readonly Ping2 _request2 = new(42);
    private readonly Ping3 _request3 = new(42);
    private readonly Ping4 _request4 = new(42);
    private IServiceProvider _provider = null!;

    /// <summary>
    /// The Scoped mediator starts caching at its eighth miss, so a scope that dispatches once, twice or seven times resolves every handler and allocates no
    /// cache, 8 is the costliest count, and 32 shows the payoff.
    /// </summary>
    [Params(1, 2, 7, 8, 32)]
    public int Dispatches { get; set; }

    [GlobalSetup]
    public void Setup() => _provider = Benchmarks.Setup.SharpScoped();

    [Benchmark(Baseline = true)]
    public async ValueTask<int> Sharp()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = 0;
        for (var i = 0; i < Dispatches; i++)
            result = await mediator.Request<Ping, int>(_request);

        return result;
    }

    [Benchmark]
    public async ValueTask<int> Sharp_Distinct()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = 0;
        for (var i = 0; i < Dispatches; i++)
            result = (i % 4) switch
            {
                0 => await mediator.Request<Ping, int>(_request),
                1 => await mediator.Request<Ping2, int>(_request2),
                2 => await mediator.Request<Ping3, int>(_request3),
                _ => await mediator.Request<Ping4, int>(_request4)
            };

        return result;
    }

    [Benchmark]
    public async ValueTask Publish()
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        for (var i = 0; i < Dispatches; i++)
            await mediator.Publish(_event);
    }
}