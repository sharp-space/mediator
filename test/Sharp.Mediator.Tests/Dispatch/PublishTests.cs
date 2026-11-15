using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Fixtures;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Dispatch;

public sealed class PublishTests
{
    private static (IMediator Mediator, TraceLog Log) Create(Action<MediatorBuilder> configure, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        var log = new TraceLog();
        services.AddSingleton(log);
        extra?.Invoke(services);
        services.AddMediator(configure);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<IMediator>(), log);
    }

    [Fact]
    public async Task TypedPublish_InvokesAllHandlers_DefaultSequential()
    {
        var (mediator, log) = Create(x => x
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        await mediator.Publish(new TestEvent("t"));

        Assert.Equal(2, log.Entries.Count);
        Assert.Contains("first:t", log.Entries);
        Assert.Contains("second:t", log.Entries);
    }

    [Fact]
    public async Task ObjectPublish_RoutesByRuntimeType()
    {
        var (mediator, log) = Create(x => x.AddHandler<FirstEventHandler>());
        await mediator.Publish((object)new TestEvent("obj"));

        Assert.Contains("first:obj", log.Entries);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    // Publish(object) takes any object, so one that is not an event is an argument error, a request included.
    public void ObjectPublish_ThrowsForNonEvent()
    {
        var (mediator, _) = Create(x => x.AddHandler<FirstEventHandler>());
        Assert.Throws<ArgumentException>(() => { _ = mediator.Publish("not-an-event"); });
        var exception = Assert.Throws<ArgumentException>(() => { _ = mediator.Publish(new EchoRequest("x")); });
        Assert.Contains("IEvent", exception.Message);
    }

    [Fact]
    public async Task SequentialPublisher_PreservesRegistrationOrder()
    {
        var (mediator, log) = Create(x => x
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>()
            .WithEventPublisher<SequentialPublisher>());

        await mediator.Publish(new TestEvent("t"));

        Assert.Equal(["first:t", "second:t"], log.Entries);
    }

    [Fact]
    public async Task PublishWithOverridePublisher_DoesNotMutateDefaultForLaterCalls()
    {
        // Regression: the event processor's publisher used to be sticky-set.
        var (mediator, log) = Create(x => x
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        var tracking = new TrackingPublisher();
        await mediator.Publish(new TestEvent("override"), tracking);

        Assert.True(tracking.WasUsed);

        // Tracking publisher invoked handlers itself, so log has events.
        Assert.Contains("first:override", log.Entries);

        // Second call without override must not use tracking publisher.
        tracking.Reset();
        await mediator.Publish(new TestEvent("default"));

        Assert.False(tracking.WasUsed);
        Assert.Contains("first:default", log.Entries);
        Assert.Contains("second:default", log.Entries);
    }

    [Fact]
    public async Task Publish_PropagatesHandlerException_DefaultSequential()
    {
        var (mediator, _) = Create(x => x.AddHandler<ThrowingEventHandler>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Publish(new TestEvent("x")).AsTask());
    }

    [Fact]
    // ParallelPublisher starts every handler before awaiting any. Each handler here waits at a gate that the test opens
    // only after counting them, so the count shows the handlers running together however busy the machine is: a
    // publisher that ran them one at a time would have started only the first.
    public async Task ParallelPublisher_RunsHandlersConcurrently()
    {
        var gate = new HandlerGate();
        var (mediator, _) = Create(
            static x => x.WithEventPublisher<ParallelPublisher>().AddHandler<FirstGatedHandler>()
                .AddHandler<SecondGatedHandler>().AddHandler<ThirdGatedHandler>(),
            services => services.AddSingleton(gate));

        var publish = mediator.Publish(new TestEvent("t"));

        Assert.Equal(3, gate.Entered);
        Assert.False(publish.IsCompleted, "every handler waits at the gate");

        gate.Open();
        await publish;
    }

    [Fact]
    public async Task EventExecutors_AreKept_OnceTheMediatorCaches()
    {
        // A mediator that caches must hand the publisher the *same* executors, and so the same chains, on successive
        // publishes; previously each publish produced fresh closures via MessageHandleBuilder.Build.
        var capture = new ExecutorCapturePublisher();
        var services = new ServiceCollection();
        services.AddSingleton(new TraceLog());
        services.AddSingleton<IEventPublisher>(capture);
        services.AddMediator(x => x
            .WithLifetime(ServiceLifetime.Singleton)
            .AddMiddleware(typeof(PassThroughMiddleware<>))
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        await mediator.Publish(new TestEvent("a"));
        await mediator.Publish(new TestEvent("b"));
        await mediator.Publish(new TestEvent("c"));

        Assert.Equal(3, capture.CallCount);
        Assert.True(capture.AllExecutorsIdentical, "The mediator should reuse its cached executors across publishes.");
    }

    // ---- abstractions as the static type ----------------------------------------------------------------------------

    [Fact]
    public async Task Publish_ThroughIEventVariables_DispatchesByRuntimeType()
    {
        var (mediator, log) = Create(x => x.AddHandler<FirstEventHandler>());

        List<IEvent> events = [new TestEvent("a"), new TestEvent("b")];
        foreach (var domainEvent in events)
            await mediator.Publish(domainEvent);

        Assert.Equal(["first:a", "first:b"], log.Entries);
    }

    [Fact]
    public async Task Publish_ThroughIEventVariable_KeepsThePublisherOverride()
    {
        var publisher = new TrackingPublisher();
        var (mediator, log) = Create(x => x.AddHandler<FirstEventHandler>());

        IEvent domainEvent = new TestEvent("a");
        await mediator.Publish(domainEvent, publisher);

        Assert.True(publisher.WasUsed);
        Assert.Equal(["first:a"], log.Entries);
    }

    [Fact]
    // An event that no assembly declares a handler for threw, from the typed entry point an InvalidOperationException
    // and from the others an ArgumentException, while one whose handlers are not registered published to none. Both
    // publish to none now, from every entry point: an event is often published before anything handles it.
    public async Task Publish_EventWithoutAHandlerAnywhere_PublishesToNoneFromEveryEntryPoint()
    {
        var (mediator, log) = Create(x => x.AddHandler<FirstEventHandler>());

        await mediator.Publish(new UnhandledEvent());
        await mediator.Publish<IEvent>(new UnhandledEvent());
        await mediator.Publish((object)new UnhandledEvent());
        await mediator.Publish(new UnhandledValueEvent());
        await mediator.Publish((object)new UnhandledValueEvent());

        Assert.Empty(log.Entries);
    }

    [Fact]
    // A publisher other than the default receives every publish, with the handlers it reaches, and so one of an event
    // that has none, configured or passed for the call.
    public async Task Publish_EventWithoutAHandlerAnywhere_HandsAnotherPublisherNoHandlers()
    {
        var configured = new ExecutorCapturePublisher();
        var passed = new ExecutorCapturePublisher();
        var (mediator, _) = Create(
            static x => x.AddHandler<FirstEventHandler>(), x => x.AddSingleton<IEventPublisher>(configured));

        await mediator.Publish((object)new UnhandledEvent());
        await mediator.Publish(new UnhandledEvent(), passed);

        Assert.Equal((1, 0), (configured.CallCount, configured.ExecutorCount));
        Assert.Equal((1, 0), (passed.CallCount, passed.ExecutorCount));
    }

    // ---- default publisher ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DefaultPublisher_NeverOverlapsHandlersSharingAScopedDependency()
    {
        // Handlers of one event resolve from one scope. Overlapping them is what EF Core reports as "a second operation
        // was started on this context", and it only shows up when both handlers actually suspend.
        var services = new ServiceCollection();
        services.AddScoped<NonConcurrentDependency>();
        services.AddMediator(x => x
            .AddHandler<FirstDependencyHandler>()
            .AddHandler<SecondDependencyHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(new TestEvent("t"));
        Assert.Equal(2, scope.ServiceProvider.GetRequiredService<NonConcurrentDependency>().Calls);
    }

    [Fact]
    public async Task DefaultPublisher_StopsAtTheFirstFailure()
    {
        var (mediator, log) = Create(x => x
            .AddHandler<ThrowingEventHandler>()
            .AddHandler<FirstEventHandler>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Publish(new TestEvent("x")).AsTask());
        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // A typed publish of an event that a mediator has cached runs the default publisher's loop at the entry point
    // itself: through an interceptor the exact one, and through the interface the general one. Each runs every handler
    // in registration order.
    public async Task CachedPublish_RunsEveryHandlerInOrder(bool exact)
    {
        var mediator = Singleton(out var log, out _, x => x
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        await Publish(mediator, new TestEvent("miss"), exact);
        await Publish(mediator, new TestEvent("hit"), exact);

        Assert.Equal(["first:miss", "second:miss", "first:hit", "second:hit"], log.Entries);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    // The loop hands the rest of a publish to an await once a handler suspends, and runs the handlers after it when
    // that handler completes: the first handler, which runs apart from the loop, or one in the loop.
    public async Task CachedPublish_RunsTheRestOnceASuspendedHandlerCompletes(bool exact, int suspended)
    {
        var mediator = Singleton(out var log, out var gate, x =>
        {
            if (suspended == 0) x.AddHandler<GatedEventHandler>();

            x.AddHandler<FirstEventHandler>();

            if (suspended == 1) x.AddHandler<GatedEventHandler>();

            x.AddHandler<SecondEventHandler>();
        });

        await Publish(mediator, new TestEvent("miss"), exact);
        gate.Close();

        var hit = Publish(mediator, new TestEvent("hit"), exact);
        Assert.False(hit.IsCompleted, "the closed gate holds the publish");

        gate.Open();
        await hit;

        string[] order = suspended == 0 ? ["gated", "first", "second"] : ["first", "gated", "second"];
        Assert.Equal([.. order.Select(x => $"{x}:miss"), .. order.Select(x => $"{x}:hit")], log.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // An event of one handler returns that handler's task as the publish's own, which completes when the handler does.
    public async Task CachedPublish_CompletesWhenItsSingleHandlerDoes(bool exact)
    {
        var mediator = Singleton(out var log, out var gate, x => x.AddHandler<GatedEventHandler>());
        await Publish(mediator, new TestEvent("miss"), exact);
        gate.Close();

        var hit = Publish(mediator, new TestEvent("hit"), exact);
        Assert.False(hit.IsCompleted, "the publish waits for its handler");

        gate.Open();
        await hit;

        Assert.Equal(["gated:miss", "gated:hit"], log.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // An event the mediator resolved no handlers for has an entry too, and a publish of it completes at once.
    public async Task CachedPublish_CompletesAtOnceForAnEventWithoutHandlers(bool exact)
    {
        var mediator = Singleton(out var log, out _, _ => { });
        await Publish(mediator, new TestEvent("miss"), exact);

        var hit = Publish(mediator, new TestEvent("hit"), exact);
        Assert.True(hit.IsCompletedSuccessfully);

        await hit;

        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // An entry keeps the publisher it was resolved with, and a cached publish through either entry point hands the
    // handlers to it when it is not the default one, whose loop the entry points run themselves.
    public async Task CachedPublish_HandsItsHandlersToAnotherPublisher(bool exact)
    {
        var mediator = Singleton(out var log, out _, x => x
            .WithEventPublisher<ParallelPublisher>()
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        await Publish(mediator, new TestEvent("miss"), exact);
        await Publish(mediator, new TestEvent("hit"), exact);

        Assert.Equal(["first:miss", "second:miss", "first:hit", "second:hit"], log.Entries);
    }

    // The exact entry point is the one an interceptor takes, from a call that names the event or from generic code. A
    // call that no interceptor reaches takes the general one: here through a delegate of the interface method, since a
    // method group is not a call; elsewhere with interceptors off, which sends both calls there.
    private static ValueTask Publish(IMediator mediator, TestEvent message, bool exact)
    {
        if (exact) return mediator.Publish(message);

        Func<TestEvent, CancellationToken, ValueTask> publish = mediator.Publish;
        return publish(message, CancellationToken.None);
    }

    // A Singleton mediator caches from its first dispatch, so the second publish of an event takes its cached entry.
    private static IMediator Singleton(out TraceLog log, out Gate gate, Action<MediatorBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(log = new TraceLog());
        services.AddSingleton(gate = new Gate());
        services.AddMediator(x => configure(x.WithLifetime(ServiceLifetime.Singleton)));
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private sealed record UnhandledEvent : IEvent;

    private readonly record struct UnhandledValueEvent : IEvent;

    // Holds GatedEventHandler while the test keeps it closed, so a publish finds that handler unfinished every time. A
    // yield does not: its continuation can finish the handler on another thread before the publish looks.
    private sealed class Gate
    {
        private TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Gate() => _source.SetResult();

        public Task Passage => _source.Task;

        public void Close() => _source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _source.SetResult();
    }

    private sealed class GatedEventHandler(TraceLog trace, Gate gate) : IEventHandler<TestEvent>
    {
        public async ValueTask Handle(TestEvent message, CancellationToken cancellationToken)
        {
            await gate.Passage;
            trace.Add($"gated:{message.Tag}");
        }
    }

    private sealed class NonConcurrentDependency
    {
        private int _active;

        public int Calls { get; private set; }

        public async ValueTask Use()
        {
            if (Interlocked.Increment(ref _active) > 1)
                throw new InvalidOperationException("A second operation was started on this instance.");

            await Task.Yield();
            Calls++;
            Interlocked.Decrement(ref _active);
        }
    }

    private sealed class FirstDependencyHandler(NonConcurrentDependency dependency) : IEventHandler<TestEvent>
    {
        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken) => dependency.Use();
    }

    private sealed class SecondDependencyHandler(NonConcurrentDependency dependency) : IEventHandler<TestEvent>
    {
        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken) => dependency.Use();
    }

    // Holds every handler that enters it until the test opens it, and counts them.
    private sealed class HandlerGate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public int Entered => Volatile.Read(ref _entered);

        public ValueTask Enter()
        {
            Interlocked.Increment(ref _entered);
            return new ValueTask(_open.Task);
        }

        public void Open() => _open.SetResult();
    }

    private sealed class FirstGatedHandler(HandlerGate gate) : IEventHandler<TestEvent>
    {
        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken) => gate.Enter();
    }

    private sealed class SecondGatedHandler(HandlerGate gate) : IEventHandler<TestEvent>
    {
        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken) => gate.Enter();
    }

    private sealed class ThirdGatedHandler(HandlerGate gate) : IEventHandler<TestEvent>
    {
        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken) => gate.Enter();
    }

    private sealed class ExecutorCapturePublisher : IEventPublisher
    {
        private EventHandlerExecutor[]? _first;

        public int CallCount { get; private set; }

        public int ExecutorCount { get; private set; }

        public bool AllExecutorsIdentical { get; private set; } = true;

        public ValueTask Publish(IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken)
        {
            CallCount++;
            ExecutorCount += executors.Count;
            if (_first is null) _first = [.. executors];
            else
            {
                for (var i = 0; i < executors.Count; i++)
                    if (!ReferenceEquals(executors[i], _first[i]))
                        AllExecutorsIdentical = false;
            }

            return ValueTask.CompletedTask;
        }
    }

    public sealed class PassThroughMiddleware<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
    {
        public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
            next(message, cancellationToken);
    }

    private sealed class TrackingPublisher : IEventPublisher
    {
        public bool WasUsed { get; private set; }

        public ValueTask Publish(IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken)
        {
            WasUsed = true;
            var tasks = new Task[executors.Count];
            for (var i = 0; i < executors.Count; i++) tasks[i] = executors[i].Invoke(message, cancellationToken).AsTask();

            return new ValueTask(Task.WhenAll(tasks));
        }

        public void Reset() => WasUsed = false;
    }

    private sealed class ThrowingEventHandler : IEventHandler<TestEvent>
    {
        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("event-handler-boom"));
    }
}