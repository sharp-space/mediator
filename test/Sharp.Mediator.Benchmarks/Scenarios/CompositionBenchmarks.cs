using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// What composing the mediator costs an application with many messages on a cold start: AddMediator, building the provider and creating the mediator,
/// each measurement a fresh process. Open-generic middleware is closed over every message the application can name, the scale fixtures' here, by
/// generated code the JIT compiles at startup, so Handlers_Middleware grows with MessageCount; it must stay a small step above Handlers.
/// </summary>
[SimpleJob(RunStrategy.ColdStart, 15, iterationCount: 1)]
[MinColumn]
[MedianColumn]
public class CompositionBenchmarks
{
    [SuppressMessage("Performance", "CA1822",
        Justification = "Preserve the instance benchmark shape so existing measurements remain comparable.")]
    [Benchmark(Baseline = true)]
    public IMediator Handlers() =>
        Setup.SharpScale(ServiceLifetime.Singleton).GetRequiredService<IMediator>();

    [SuppressMessage("Performance", "CA1822",
        Justification = "Preserve the instance benchmark shape so existing measurements remain comparable.")]
    [Benchmark]
    public IMediator Handlers_Middleware() =>
        Setup.SharpScale(ServiceLifetime.Singleton, true)
            .GetRequiredService<IMediator>();
}