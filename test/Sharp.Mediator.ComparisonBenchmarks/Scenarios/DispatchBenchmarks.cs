using BenchmarkDotNet.Attributes;
using Sharp.Mediator.ComparisonBenchmarks.Fixtures;
using SharpFirst = Sharp.Mediator.ComparisonBenchmarks.Sharp.FirstMessage;
using SharpFirstResponse = Sharp.Mediator.ComparisonBenchmarks.Sharp.FirstMessageResponse;
using SharpLast = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastMessage;
using SharpLastResponse = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastMessageResponse;
using MartinFirst = Sharp.Mediator.ComparisonBenchmarks.Martin.FirstMessage;
using MartinFirstResponse = Sharp.Mediator.ComparisonBenchmarks.Martin.FirstMessageResponse;
using MartinLast = Sharp.Mediator.ComparisonBenchmarks.Martin.LastMessage;
using MartinLastResponse = Sharp.Mediator.ComparisonBenchmarks.Martin.LastMessageResponse;
using MediatRFirst = Sharp.Mediator.ComparisonBenchmarks.MediatR.FirstMessage;
using MediatRFirstResponse = Sharp.Mediator.ComparisonBenchmarks.MediatR.FirstMessageResponse;
using MediatRLast = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastMessage;
using MediatRLastResponse = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastMessageResponse;

namespace Sharp.Mediator.ComparisonBenchmarks.Scenarios;

/// <summary>
/// One message type in a loop, which is each library's best case: PGO sees a single target. Sharp_Interface, Mediator_Interface, and MediatR_Interface
/// are the like-for-like set, each calling through its library's public interface and inferring the response. Mediator_Concrete calls the per-message
/// overload of Mediator's generated class, its fastest call, which Mediator recommends for speed; code that depends on that class instead of the
/// interface cannot be mocked, so applications rarely take it.
/// </summary>
public class DispatchBenchmarks : WarmMediatorBenchmark
{
    private readonly MartinFirst _martinFirst = new();
    private readonly MartinLast _martinLast = new();
    private readonly object _martinObject = new MartinLast();
    private readonly MediatRFirst _mediatRFirst = new();
    private readonly MediatRLast _mediatRLast = new();
    private readonly object _mediatRObject = new MediatRLast();
    private readonly SharpFirst _sharpFirst = new();
    private readonly SharpLast _sharpLast = new();
    private readonly object _sharpObject = new SharpLast();

    [Benchmark]
    public ValueTask<SharpFirstResponse> Sharp_Interface_First() => SharpInterface.Request(_sharpFirst);

    [Benchmark]
    public ValueTask<SharpLastResponse> Sharp_Interface_Last() => SharpInterface.Request(_sharpLast);

    [Benchmark]
    public ValueTask<object?> Sharp_Object_Last() => SharpInterface.Request(_sharpObject);

    [Benchmark]
    public ValueTask<MartinFirstResponse> Mediator_Interface_First() => MartinInterface.Send(_martinFirst);

    [Benchmark]
    public ValueTask<MartinLastResponse> Mediator_Interface_Last() => MartinInterface.Send(_martinLast);

    [Benchmark]
    public ValueTask<object?> Mediator_Object_Last() => MartinInterface.Send(_martinObject);

    [Benchmark]
    public ValueTask<MartinFirstResponse> Mediator_Concrete_First() => MartinConcrete.Send(_martinFirst);

    [Benchmark]
    public ValueTask<MartinLastResponse> Mediator_Concrete_Last() => MartinConcrete.Send(_martinLast);

    [Benchmark]
    public Task<MediatRFirstResponse> MediatR_Interface_First() => MediatRInterface.Send(_mediatRFirst);

    [Benchmark]
    public Task<MediatRLastResponse> MediatR_Interface_Last() => MediatRInterface.Send(_mediatRLast);

    [Benchmark]
    public Task<object?> MediatR_Object_Last() => MediatRInterface.Send(_mediatRObject);
}