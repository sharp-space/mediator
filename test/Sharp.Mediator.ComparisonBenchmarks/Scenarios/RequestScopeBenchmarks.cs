using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.ComparisonBenchmarks.Fixtures;
using MartinMediator = Mediator;
using MediatRMediator = MediatR;
using SharpLast = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastMessage;
using SharpLastResponse = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastMessageResponse;
using MartinLast = Sharp.Mediator.ComparisonBenchmarks.Martin.LastMessage;
using MartinLastResponse = Sharp.Mediator.ComparisonBenchmarks.Martin.LastMessageResponse;
using MediatRLast = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastMessage;
using MediatRLastResponse = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastMessageResponse;

namespace Sharp.Mediator.ComparisonBenchmarks.Scenarios;

/// <summary>What one web request pays: a new scope, the mediator resolved from it, one dispatch, and disposal.</summary>
[MemoryDiagnoser]
[Config(typeof(QuantilesConfig))]
public class RequestScopeBenchmarks
{
    private readonly MartinLast _martinLast = new();
    private readonly MediatRLast _mediatRLast = new();

    private readonly SharpLast _sharpLast = new();
    private ServiceProvider _sharpProvider = null!, _martinProvider = null!, _mediatRProvider = null!;
    private IServiceScopeFactory _sharpScopes = null!, _martinScopes = null!, _mediatRScopes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sharpProvider = ComparisonBenchmarks.Setup.Sharp();
        _martinProvider = ComparisonBenchmarks.Setup.Martin();
        _mediatRProvider = ComparisonBenchmarks.Setup.MediatR();
        _sharpScopes = _sharpProvider.GetRequiredService<IServiceScopeFactory>();
        _martinScopes = _martinProvider.GetRequiredService<IServiceScopeFactory>();
        _mediatRScopes = _mediatRProvider.GetRequiredService<IServiceScopeFactory>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sharpProvider.Dispose();
        _martinProvider.Dispose();
        _mediatRProvider.Dispose();
    }

    [Benchmark]
    public async ValueTask<SharpLastResponse> Sharp()
    {
        await using var scope = _sharpScopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Request(_sharpLast);
    }

    [Benchmark]
    public async ValueTask<MartinLastResponse> Mediator()
    {
        await using var scope = _martinScopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MartinMediator.IMediator>().Send(_martinLast);
    }

    [Benchmark]
    public async ValueTask<MediatRLastResponse> MediatR()
    {
        await using var scope = _mediatRScopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediatRMediator.IMediator>().Send(_mediatRLast);
    }
}