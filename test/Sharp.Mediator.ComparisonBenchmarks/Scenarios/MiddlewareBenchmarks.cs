using BenchmarkDotNet.Attributes;
using Sharp.Mediator.ComparisonBenchmarks.Fixtures;
using SharpLast = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastMessage;
using SharpLastResponse = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastMessageResponse;
using MartinLast = Sharp.Mediator.ComparisonBenchmarks.Martin.LastMessage;
using MartinLastResponse = Sharp.Mediator.ComparisonBenchmarks.Martin.LastMessageResponse;
using MediatRLast = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastMessage;
using MediatRLastResponse = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastMessageResponse;

namespace Sharp.Mediator.ComparisonBenchmarks.Scenarios;

/// <summary>
/// One pass-through middleware / behavior in front of the handler, called through the public interface, and for Mediator_Concrete through the
/// per-message overload of Mediator's generated class.
/// </summary>
public class MiddlewareBenchmarks : WarmMediatorBenchmark
{
    private readonly MartinLast _martinLast = new();
    private readonly MediatRLast _mediatRLast = new();
    private readonly SharpLast _sharpLast = new();

    protected override bool WithPassThrough => true;

    [Benchmark]
    public ValueTask<SharpLastResponse> Sharp() => SharpInterface.Request(_sharpLast);

    [Benchmark]
    public ValueTask<MartinLastResponse> Mediator() => MartinInterface.Send(_martinLast);

    [Benchmark]
    public ValueTask<MartinLastResponse> Mediator_Concrete() => MartinConcrete.Send(_martinLast);

    [Benchmark]
    public Task<MediatRLastResponse> MediatR() => MediatRInterface.Send(_mediatRLast);
}