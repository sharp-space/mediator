using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.ScaleFixtures;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// A scope that lives long in an application with many messages, such as one a background service keeps: warm dispatches through a Scoped mediator that
/// cached its chains long ago. The process dispatches every message first, the first one first and the last one last, so the first message holds the
/// lowest slot and the last the highest. A scope keeps the chains of high slots beside its array while it caches few, which must not leave a long-lived
/// scope slower for the last message than for the first.
/// </summary>
[MemoryDiagnoser]
public class LongLivedScopeBenchmarks
{
    private readonly FirstMessage _first = new();
    private readonly LastMessage _last = new();
    private IMediator _mediator = null!;
    private IServiceScope _scope = null!;

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [GlobalSetup]
    public void Setup()
    {
        var provider = Benchmarks.Setup.SharpScale(ServiceLifetime.Scoped);

        // Through the object entry point, as in ScopeCacheBenchmarks, which takes a message's slot when it dispatches
        // the message: a typed call can take it as early as when the method it is in is compiled.
        using (var scope = provider.CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            foreach (var type in typeof(FirstMessage).Assembly.GetTypes()
                         .Where(static x => typeof(IRequest<int>).IsAssignableFrom(x))
                         .OrderBy(static x => x == typeof(FirstMessage) ? 0 : x == typeof(LastMessage) ? 2 : 1))
                _ = mediator.Request(Activator.CreateInstance(type)!).GetAwaiter().GetResult();
        }

        _scope = provider.CreateScope();
        _mediator = _scope.ServiceProvider.GetRequiredService<IMediator>();
    }

    [GlobalCleanup]
    public void Cleanup() => _scope.Dispose();

    [Benchmark(Baseline = true)]
    public ValueTask<int> First() => _mediator.Request<FirstMessage, int>(_first);

    [Benchmark]
    public ValueTask<int> Last() => _mediator.Request<LastMessage, int>(_last);
}