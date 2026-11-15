using BenchmarkDotNet.Attributes;
using Sharp.Mediator.ComparisonBenchmarks.Fixtures;
using MartinMediator = Mediator;
using SharpFirst = Sharp.Mediator.ComparisonBenchmarks.Sharp.FirstCommand;
using SharpLast = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastCommand;
using MartinFirst = Sharp.Mediator.ComparisonBenchmarks.Martin.FirstCommand;
using MartinLast = Sharp.Mediator.ComparisonBenchmarks.Martin.LastCommand;
using MediatRFirst = Sharp.Mediator.ComparisonBenchmarks.MediatR.FirstCommand;
using MediatRLast = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastCommand;

namespace Sharp.Mediator.ComparisonBenchmarks.Scenarios;

/// <summary>
/// A request without a response, one message type in a loop as in <see cref="DispatchBenchmarks" />, called through each library's public interface.
/// Mediator sends it as a command, which completes with its <c>Unit</c>; Mediator_Concrete sends it through the per-message overload of Mediator's
/// generated class, as in DispatchBenchmarks.
/// </summary>
public class VoidRequestBenchmarks : WarmMediatorBenchmark
{
    private readonly MartinFirst _martinFirst = new();
    private readonly MartinLast _martinLast = new();
    private readonly MediatRFirst _mediatRFirst = new();
    private readonly MediatRLast _mediatRLast = new();
    private readonly SharpFirst _sharpFirst = new();
    private readonly SharpLast _sharpLast = new();

    [Benchmark]
    public ValueTask Sharp_First() => SharpInterface.Request(_sharpFirst);

    [Benchmark]
    public ValueTask Sharp_Last() => SharpInterface.Request(_sharpLast);

    [Benchmark]
    public ValueTask<MartinMediator.Unit> Mediator_First() => MartinInterface.Send(_martinFirst);

    [Benchmark]
    public ValueTask<MartinMediator.Unit> Mediator_Last() => MartinInterface.Send(_martinLast);

    [Benchmark]
    public ValueTask<MartinMediator.Unit> Mediator_Concrete_First() => MartinConcrete.Send(_martinFirst);

    [Benchmark]
    public ValueTask<MartinMediator.Unit> Mediator_Concrete_Last() => MartinConcrete.Send(_martinLast);

    [Benchmark]
    public Task MediatR_First() => MediatRInterface.Send(_mediatRFirst);

    [Benchmark]
    public Task MediatR_Last() => MediatRInterface.Send(_mediatRLast);
}