using BenchmarkDotNet.Attributes;
using Sharp.Mediator.ComparisonBenchmarks.Fixtures;
using SharpFirst = Sharp.Mediator.ComparisonBenchmarks.Sharp.FirstEvent;
using SharpLast = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastEvent;
using MartinFirst = Sharp.Mediator.ComparisonBenchmarks.Martin.FirstEvent;
using MartinLast = Sharp.Mediator.ComparisonBenchmarks.Martin.LastEvent;
using MediatRFirst = Sharp.Mediator.ComparisonBenchmarks.MediatR.FirstEvent;
using MediatRLast = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastEvent;

namespace Sharp.Mediator.ComparisonBenchmarks.Scenarios;

/// <summary>
/// An event with two handlers, one event type in a loop as in <see cref="DispatchBenchmarks" />, published through each library's public interface, and
/// Mediator_Concrete through the per-message overload of Mediator's generated class, as in DispatchBenchmarks. Each library's default publisher runs the
/// handlers one after the other.
/// </summary>
public class PublishBenchmarks : WarmMediatorBenchmark
{
    private readonly MartinFirst _martinFirst = new();
    private readonly MartinLast _martinLast = new();
    private readonly MediatRFirst _mediatRFirst = new();
    private readonly MediatRLast _mediatRLast = new();
    private readonly SharpFirst _sharpFirst = new();
    private readonly SharpLast _sharpLast = new();

    [Benchmark]
    public ValueTask Sharp_First() => SharpInterface.Publish(_sharpFirst);

    [Benchmark]
    public ValueTask Sharp_Last() => SharpInterface.Publish(_sharpLast);

    [Benchmark]
    public ValueTask Mediator_First() => MartinInterface.Publish(_martinFirst);

    [Benchmark]
    public ValueTask Mediator_Last() => MartinInterface.Publish(_martinLast);

    [Benchmark]
    public ValueTask Mediator_Concrete_First() => MartinConcrete.Publish(_martinFirst);

    [Benchmark]
    public ValueTask Mediator_Concrete_Last() => MartinConcrete.Publish(_martinLast);

    [Benchmark]
    public Task MediatR_First() => MediatRInterface.Publish(_mediatRFirst);

    [Benchmark]
    public Task MediatR_Last() => MediatRInterface.Publish(_mediatRLast);
}