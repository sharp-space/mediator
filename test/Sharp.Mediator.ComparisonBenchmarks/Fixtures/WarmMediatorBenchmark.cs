using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MartinMediator = Mediator;
using MediatRMediator = MediatR;

namespace Sharp.Mediator.ComparisonBenchmarks.Fixtures;

/// <summary>
/// One mediator of each library with its dependencies resolved, inside a scope that stays open on the Scoped lifetime. These scenarios measure dispatch;
/// <see cref="Scenarios.RequestScopeBenchmarks" /> measures the scope around it.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(QuantilesConfig))]
public abstract class WarmMediatorBenchmark
{
    protected MartinMediator.Mediator MartinConcrete = null!;
    protected MartinMediator.IMediator MartinInterface = null!;
    protected MediatRMediator.IMediator MediatRInterface = null!;

    protected IMediator SharpInterface = null!;
    private ServiceProvider _sharpProvider = null!, _martinProvider = null!, _mediatRProvider = null!;
    private IServiceScope _sharpScope = null!, _martinScope = null!, _mediatRScope = null!;

    protected virtual bool WithPassThrough => false;

    [GlobalSetup]
    public void Setup()
    {
        _sharpProvider = ComparisonBenchmarks.Setup.Sharp(WithPassThrough);
        _martinProvider = ComparisonBenchmarks.Setup.Martin(WithPassThrough);
        _mediatRProvider = ComparisonBenchmarks.Setup.MediatR(WithPassThrough);
        _sharpScope = _sharpProvider.CreateScope();
        _martinScope = _martinProvider.CreateScope();
        _mediatRScope = _mediatRProvider.CreateScope();
        SharpInterface = _sharpScope.ServiceProvider.GetRequiredService<IMediator>();
        MartinInterface = _martinScope.ServiceProvider.GetRequiredService<MartinMediator.IMediator>();
        MartinConcrete = _martinScope.ServiceProvider.GetRequiredService<MartinMediator.Mediator>();
        MediatRInterface = _mediatRScope.ServiceProvider.GetRequiredService<MediatRMediator.IMediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sharpScope.Dispose();
        _martinScope.Dispose();
        _mediatRScope.Dispose();
        _sharpProvider.Dispose();
        _martinProvider.Dispose();
        _mediatRProvider.Dispose();
    }
}