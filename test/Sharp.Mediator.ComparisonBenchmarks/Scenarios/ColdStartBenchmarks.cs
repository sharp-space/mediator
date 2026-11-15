using System.Diagnostics.CodeAnalysis;
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
/// Registration through the first response, once per process, so type loading and JIT are all first-time. The libraries share this process's module
/// initializers, which ran before the measurement; StartupBenchmarks in Sharp.Mediator.Benchmarks measures Sharp's on its own.
/// </summary>
[SimpleJob(RunStrategy.ColdStart, 15, iterationCount: 1)]
[MinColumn]
[MedianColumn]
public class ColdStartBenchmarks
{
    [SuppressMessage("Performance", "CA1822",
        Justification = "Preserve the instance benchmark shape so existing measurements remain comparable.")]
    [Benchmark]
    public async Task<SharpLastResponse> Sharp()
    {
        await using var provider = Setup.Sharp();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Request(new SharpLast());
    }

    [SuppressMessage("Performance", "CA1822",
        Justification = "Preserve the instance benchmark shape so existing measurements remain comparable.")]
    [Benchmark]
    public async Task<MartinLastResponse> Mediator()
    {
        await using var provider = Setup.Martin();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MartinMediator.IMediator>().Send(new MartinLast());
    }

    [SuppressMessage("Performance", "CA1822",
        Justification = "Preserve the instance benchmark shape so existing measurements remain comparable.")]
    [Benchmark]
    public async Task<MediatRLastResponse> MediatR()
    {
        await using var provider = Setup.MediatR();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediatRMediator.IMediator>().Send(new MediatRLast());
    }
}