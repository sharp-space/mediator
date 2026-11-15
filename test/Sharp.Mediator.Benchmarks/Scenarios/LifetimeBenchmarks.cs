using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// One request under each lifetime, the way a web request meets it: a new scope, the mediator resolved from it, and one typed dispatch. A Singleton
/// mediator dispatches through the chain it cached. A Scoped mediator and a Transient one are created for the request and resolve a new handler, since a
/// single dispatch does not make a Scoped mediator cache and a Transient one never does. The mixed cases register the handler with a lifetime of its
/// own: a Singleton mediator resolves a Transient handler for every dispatch rather than keep it, and a Scoped mediator resolves a Singleton handler,
/// which the container created once.
/// </summary>
[MemoryDiagnoser]
public class LifetimeBenchmarks
{
    private readonly Ping _request = new(42);
    private IServiceProvider _singleton = null!, _scoped = null!, _transient = null!;
    private IServiceProvider _singletonTransientHandler = null!, _scopedSingletonHandler = null!;

    [GlobalSetup]
    public void Setup()
    {
        _singleton = Benchmarks.Setup.SharpLifetimes(ServiceLifetime.Singleton, ServiceLifetime.Singleton);
        _scoped = Benchmarks.Setup.SharpLifetimes(ServiceLifetime.Scoped, ServiceLifetime.Scoped);
        _transient = Benchmarks.Setup.SharpLifetimes(ServiceLifetime.Transient, ServiceLifetime.Transient);
        _singletonTransientHandler =
            Benchmarks.Setup.SharpLifetimes(ServiceLifetime.Singleton, ServiceLifetime.Transient);

        _scopedSingletonHandler =
            Benchmarks.Setup.SharpLifetimes(ServiceLifetime.Scoped, ServiceLifetime.Singleton);
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Singleton() => Dispatch(_singleton);

    [Benchmark]
    public ValueTask<int> Scoped() => Dispatch(_scoped);

    [Benchmark]
    public ValueTask<int> Transient() => Dispatch(_transient);

    [Benchmark]
    public ValueTask<int> Singleton_TransientHandler() => Dispatch(_singletonTransientHandler);

    [Benchmark]
    public ValueTask<int> Scoped_SingletonHandler() => Dispatch(_scopedSingletonHandler);

    private async ValueTask<int> Dispatch(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Request<Ping, int>(_request);
    }
}