using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using MartinMediator = Mediator;
using MediatRMediator = MediatR;
using SharpLast = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastMessage;
using SharpLastResponse = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastMessageResponse;
using MartinLast = Sharp.Mediator.ComparisonBenchmarks.Martin.LastMessage;
using MartinLastResponse = Sharp.Mediator.ComparisonBenchmarks.Martin.LastMessageResponse;
using MediatRLast = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastMessage;
using MediatRLastResponse = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastMessageResponse;

namespace Sharp.Mediator.ComparisonBenchmarks.Scenarios;

/// <summary>
/// The first dispatch in a process, after the module initializers have run and each library's mediator is resolved: ColdStartBenchmarks measures
/// registration through the first response, and DispatchBenchmarks the same calls warm. Each process sets up every library, so one library's first
/// dispatch includes only its own work.
/// </summary>
[SimpleJob(RunStrategy.ColdStart, 15, iterationCount: 1)]
[MinColumn]
[MedianColumn]
public class FirstDispatchBenchmarks
{
    private readonly MartinLast _martinLast = new();
    private readonly MediatRLast _mediatRLast = new();

    private readonly SharpLast _sharpLast = new();
    private MartinMediator.IMediator _martin = null!;
    private MediatRMediator.IMediator _mediatR = null!;
    private IMediator _sharp = null!;
    // The setup of WarmMediatorBenchmark, without its memory diagnoser, which would report the dispatch
    // after the first.
    private ServiceProvider _sharpProvider = null!, _martinProvider = null!, _mediatRProvider = null!;
    private IServiceScope _sharpScope = null!, _martinScope = null!, _mediatRScope = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sharpProvider = ComparisonBenchmarks.Setup.Sharp();
        _martinProvider = ComparisonBenchmarks.Setup.Martin();
        _mediatRProvider = ComparisonBenchmarks.Setup.MediatR();
        _sharpScope = _sharpProvider.CreateScope();
        _martinScope = _martinProvider.CreateScope();
        _mediatRScope = _mediatRProvider.CreateScope();
        _sharp = _sharpScope.ServiceProvider.GetRequiredService<IMediator>();
        _martin = _martinScope.ServiceProvider.GetRequiredService<MartinMediator.IMediator>();
        _mediatR = _mediatRScope.ServiceProvider.GetRequiredService<MediatRMediator.IMediator>();
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

    [Benchmark]
    public ValueTask<SharpLastResponse> Sharp() => _sharp.Request(_sharpLast);

    [Benchmark]
    public ValueTask<MartinLastResponse> Mediator() => _martin.Send(_martinLast);

    [Benchmark]
    public Task<MediatRLastResponse> MediatR() => _mediatR.Send(_mediatRLast);
}