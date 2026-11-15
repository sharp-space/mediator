using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// A request and an event whose handlers complete synchronously, asynchronously, with a failure, or canceled, on a warm Singleton mediator. A typed
/// request returns the handler's task as it is, and the object entry point adapts one that did not complete successfully. The default publisher awaits
/// the event's two handlers in turn and stops at the first failure; ParallelPublisher starts both before it awaits either. An asynchronous handler
/// yields to the thread pool. A failure or a cancellation throws from the await, and the scenario catches it.
/// </summary>
[MemoryDiagnoser]
public class CompletionBenchmarks
{
    private CancellationToken _cancellationToken;
    private Completed _event = null!;
    private Complete _request = null!;
    private IMediator _sequential = null!, _parallel = null!;

    /// <summary>How the handlers complete.</summary>
    [ParamsAllValues]
    public Completion Completion { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sequential = Benchmarks.Setup.SharpWith(static x => x
                .AddHandler<CompleteHandler>()
                .AddHandler<FirstCompletedHandler>()
                .AddHandler<SecondCompletedHandler>())
            .GetRequiredService<IMediator>();

        _parallel = Benchmarks.Setup.SharpWith(static x => x
                .AddHandler<FirstCompletedHandler>()
                .AddHandler<SecondCompletedHandler>()
                .WithEventPublisher<ParallelPublisher>())
            .GetRequiredService<IMediator>();

        _request = new(Completion);
        _event = new(Completion);
        _cancellationToken = new CancellationToken(Completion == Completion.Canceled);
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Request_Typed() =>
        Outcome(_sequential.Request<Complete, int>(_request, _cancellationToken));

    [Benchmark]
    public ValueTask<int> Request_Object() => Outcome(_sequential.Request((object)_request, _cancellationToken));

    [Benchmark]
    public ValueTask<int> Publish_Sequential() => Outcome(_sequential.Publish(_event, _cancellationToken));

    [Benchmark]
    public ValueTask<int> Publish_Parallel() => Outcome(_parallel.Publish(_event, _cancellationToken));

    // 0 when the call completed and 1 when it failed or was canceled, so that --check shows how each scenario ended.
    private static async ValueTask<int> Outcome<T>(ValueTask<T> task)
    {
        try
        {
            await task;
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            return 1;
        }
    }

    // A publish can also throw an AggregateException: ParallelPublisher throws the failures of both handlers together.
    private static async ValueTask<int> Outcome(ValueTask task)
    {
        try
        {
            await task;
            return 0;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or OperationCanceledException or AggregateException)
        {
            return 1;
        }
    }
}