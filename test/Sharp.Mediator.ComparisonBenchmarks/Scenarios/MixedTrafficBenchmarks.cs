using BenchmarkDotNet.Attributes;
using Sharp.Mediator.ComparisonBenchmarks.Fixtures;
using SharpRotation = Sharp.Mediator.ComparisonBenchmarks.Sharp.Rotation;
using MartinRotation = Sharp.Mediator.ComparisonBenchmarks.Martin.Rotation;
using MediatRRotation = Sharp.Mediator.ComparisonBenchmarks.MediatR.Rotation;

namespace Sharp.Mediator.ComparisonBenchmarks.Scenarios;

/// <summary>
/// Up to 64 message types through distinct call sites in one benchmark, so shared generic code goes polymorphic the way it does in an application, and
/// no library gets the monomorphic case of <see cref="DispatchBenchmarks" />. Mediator_Concrete makes the same calls through Mediator's generated class,
/// where each binds to its message's own overload.
/// </summary>
public class MixedTrafficBenchmarks : WarmMediatorBenchmark
{
    [Benchmark(OperationsPerInvoke = SharpRotation.Size)]
    public int Sharp_Interface() => SharpRotation.Interface(SharpInterface);

    [Benchmark(OperationsPerInvoke = MartinRotation.Size)]
    public int Mediator_Interface() => MartinRotation.Interface(MartinInterface);

    [Benchmark(OperationsPerInvoke = MartinRotation.Size)]
    public int Mediator_Concrete() => MartinRotation.Concrete(MartinConcrete);

    [Benchmark(OperationsPerInvoke = MediatRRotation.Size)]
    public int MediatR_Interface() => MediatRRotation.Interface(MediatRInterface);
}