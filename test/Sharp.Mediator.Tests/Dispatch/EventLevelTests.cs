using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Dispatch;

// An event reaches every handler declared for a type it is: its own, its base classes', and its interfaces'. Each such
// type is a level, published with its own handlers and middleware, in order from the event's own type outward. The
// messages are public so interceptors can name them, and an interface of their own stands in for IEvent: a handler
// declared for IEvent would give every event in this process a level.
public sealed class EventLevelTests
{
    // More rounds than a Scoped mediator misses before it caches, so the warm path answers too.
    private const int Rounds = 10;

    public static TheoryData<ServiceLifetime> Lifetimes => [ServiceLifetime.Singleton, ServiceLifetime.Scoped, ServiceLifetime.Transient];

    private static IMediator Mediator(
        ServiceLifetime lifetime,
        Action<MediatorBuilder> configure,
        LevelLog log,
        Action<IServiceCollection>? services = null
    )
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(log);
        services?.Invoke(collection);
        collection.AddMediator(x => configure(x.WithLifetime(lifetime)));
        return collection.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
    }

    private static IMediator Levels(ServiceLifetime lifetime, LevelLog log, Action<MediatorBuilder>? more = null) =>
        Mediator(lifetime, x =>
        {
            x.AddHandler<LevelDerivedHandler>().AddHandler<LevelBaseHandler>().AddHandler<LevelAuditedHandler>()
                .AddHandler<LevelTrackedHandler>();

            more?.Invoke(x);
        }, log);

    [Theory]
    [MemberData(nameof(Lifetimes))]
    public async Task EveryEntryPoint_ReachesEveryLevel_InOrder(ServiceLifetime lifetime)
    {
        var log = new LevelLog();
        var mediator = Levels(lifetime, log);
        string[] levels = ["derived", "base", "audited", "tracked"];
        for (var round = 0; round < Rounds; round++)
        {
            await mediator.Publish(new LevelDerived());

            Assert.Equal(levels, log.Take());

            await mediator.Publish<LevelBase>(new LevelDerived());

            Assert.Equal(levels, log.Take());

            await mediator.Publish<ILevelAudited>(new LevelDerived());

            Assert.Equal(levels, log.Take());

            IEvent message = new LevelDerived();

            await mediator.Publish(message);

            Assert.Equal(levels, log.Take());

            await mediator.Publish((object)new LevelDerived());

            Assert.Equal(levels, log.Take());
        }
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    // A base instance has fewer levels than its derived types, whichever was published first.
    public async Task BaseInstance_ReachesOnlyItsOwnLevels(ServiceLifetime lifetime)
    {
        var log = new LevelLog();
        var mediator = Levels(lifetime, log);
        for (var round = 0; round < Rounds; round++)
        {
            await mediator.Publish(new LevelDerived());
            log.Take();

            await mediator.Publish(new LevelBase());

            Assert.Equal(["base"], log.Take());

            await mediator.Publish(new LevelBase());

            Assert.Equal(["base"], log.Take());
        }
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    // Once an event of exactly LevelBase is recorded as reaching its own level alone, a typed publish of one goes
    // straight to that level's handlers. A derived instance passed as a LevelBase still reaches every level, cached
    // or not, with or without a publisher named. The base is published first, so it is recorded whichever tests ran
    // before this one.
    public async Task DerivedInstance_ThroughABaseRecordedComplete_ReachesEveryLevel(ServiceLifetime lifetime)
    {
        var log = new LevelLog();
        var mediator = Levels(lifetime, log);
        string[] levels = ["derived", "base", "audited", "tracked"];

        await mediator.Publish(new LevelBase());

        Assert.Equal(["base"], log.Take());
        Assert.True(MessageRoutes.IsComplete<LevelBase>());

        for (var round = 0; round < Rounds; round++)
        {
            await mediator.Publish<LevelBase>(new LevelDerived());

            Assert.Equal(levels, log.Take());

            await mediator.Publish<LevelBase>(new LevelDerived(), new SequentialPublisher());

            Assert.Equal(levels, log.Take());
        }
    }

    [Fact]
    public async Task UnregisteredLevel_ContributesNothing()
    {
        var log = new LevelLog();
        var mediator = Mediator(ServiceLifetime.Singleton, static x => x.AddHandler<LevelDerivedHandler>(), log);

        await mediator.Publish<LevelBase>(new LevelDerived());
        await mediator.Publish(new LevelBase());

        Assert.Equal(["derived"], log.Take());
    }

    [Fact]
    public async Task SequentialPublisher_StopsAtTheFirstFailingLevel()
    {
        var log = new LevelLog { FailOn = "base" };
        var mediator = Levels(ServiceLifetime.Singleton, log);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await mediator.Publish(new LevelDerived()));
        Assert.Equal(["derived"], log.Take());
    }

    [Fact]
    public async Task SequentialPublisher_WaitsForALevelThatSuspends()
    {
        var log = new LevelLog { SuspendOn = "derived" };
        var mediator = Levels(ServiceLifetime.Singleton, log);

        var publish = mediator.Publish(new LevelDerived());
        Assert.False(publish.IsCompleted, "the first level is suspended");
        Assert.Equal(1, log.Suspensions);
        Assert.Empty(log.Take());

        log.Release();
        await publish;

        Assert.Equal(["derived", "base", "audited", "tracked"], log.Take());
    }

    [Theory]
    [InlineData("derived")]
    [InlineData("base")]
    public async Task ParallelPublisher_RunsEveryLevel_WhenOneFails(string failing)
    {
        var log = new LevelLog { FailOn = failing };
        var mediator = Levels(ServiceLifetime.Singleton, log, static x => x.WithEventPublisher<ParallelPublisher>());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await mediator.Publish(new LevelDerived()));

        string[] others = ["audited", "base", "derived", "tracked"];
        Assert.Equal(others.Where(x => x != failing), log.Take().Order(StringComparer.Ordinal));
    }

    [Fact]
    // Every level's failure is reported, not only the first one's.
    public async Task ParallelPublisher_ThrowsTheFailuresOfEveryLevelTogether()
    {
        var log = new LevelLog { FailOn = "*" };
        var mediator = Levels(ServiceLifetime.Singleton, log, static x => x.WithEventPublisher<ParallelPublisher>());
        var exception = await Assert.ThrowsAsync<AggregateException>(async () => await mediator.Publish(new LevelDerived()));
        Assert.Equal(["derived", "base", "audited", "tracked"], exception.InnerExceptions.Select(static x => x.Message));
    }

    [Fact]
    public async Task ParallelPublisher_CompletesSynchronously_WhenEveryLevelDoes()
    {
        var log = new LevelLog();
        var mediator = Levels(ServiceLifetime.Singleton, log, static x => x.WithEventPublisher<ParallelPublisher>());

        var publish = mediator.Publish(new LevelDerived());
        Assert.True(publish.IsCompletedSuccessfully);

        await publish;

        Assert.Equal(["audited", "base", "derived", "tracked"], log.Take().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ParallelPublisher_AwaitsEverySuspendedLevel()
    {
        var log = new LevelLog { SuspendOn = "*" };
        var mediator = Levels(ServiceLifetime.Singleton, log, static x => x.WithEventPublisher<ParallelPublisher>());

        var publish = mediator.Publish(new LevelDerived());
        Assert.False(publish.IsCompleted, "every level is suspended");
        Assert.Equal(4, log.Suspensions);

        log.Release();
        await publish;

        Assert.Equal(["audited", "base", "derived", "tracked"], log.Take().Order(StringComparer.Ordinal));
    }

    [Fact]
    // A publisher of the application's own is called once per publish, with every level's handlers in level order.
    public async Task CustomPublisher_ReceivesEveryLevelInOneCall()
    {
        var log = new LevelLog();
        var publisher = new LevelCountingPublisher();
        var mediator = Mediator(
            ServiceLifetime.Singleton,
            static x => x.AddHandler<LevelDerivedHandler>().AddHandler<LevelBaseHandler>().AddHandler<LevelAuditedHandler>()
                .AddHandler<LevelTrackedHandler>(),
            log,
            services => services.AddSingleton<IEventPublisher>(publisher));

        await mediator.Publish(new LevelDerived());
        await mediator.Publish(new LevelDerived(), new LevelCountingPublisher());

        Assert.Equal(["LevelDerived LevelBase ILevelAudited ILevelTracked"], publisher.Calls);
        Assert.Equal(["derived", "base", "audited", "tracked", "derived", "base", "audited", "tracked"], log.Take());
    }

    [Fact]
    // Once a mediator keeps each level's executors, it hands a publisher that receives every level the same array on
    // each publish, where it used to collect them into a new list every time.
    public async Task CustomPublisher_ReceivesTheSameExecutorsOnEachWarmPublish()
    {
        var publisher = new LevelCountingPublisher();
        var mediator = Mediator(
            ServiceLifetime.Singleton,
            static x => x.AddHandler<LevelDerivedHandler>().AddHandler<LevelBaseHandler>(),
            new LevelLog(),
            services => services.AddSingleton<IEventPublisher>(publisher));

        await mediator.Publish(new LevelDerived());
        var first = publisher.Last;
        await mediator.Publish(new LevelDerived());

        Assert.Same(first, publisher.Last);
        Assert.Equal([typeof(LevelDerived), typeof(LevelBase)], first.Select(static x => x.EventType));
    }

    [Fact]
    // Every level reaches a custom publisher in one call, so it alone decides how they run: this one starts every
    // handler before awaiting any.
    public async Task CustomPublisher_DecidesHowTheLevelsRunTogether()
    {
        var log = new LevelLog { SuspendOn = "*" };
        var mediator = Mediator(
            ServiceLifetime.Singleton,
            static x => x.AddHandler<LevelDerivedHandler>().AddHandler<LevelBaseHandler>().AddHandler<LevelAuditedHandler>()
                .AddHandler<LevelTrackedHandler>(), log,
            static services => services.AddSingleton<IEventPublisher>(new StartingPublisher()));

        var publish = mediator.Publish(new LevelDerived());

        Assert.Equal(4, log.Suspensions);

        log.Release();
        await publish;

        Assert.Equal(["audited", "base", "derived", "tracked"], log.Take().Order(StringComparer.Ordinal));
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    // An executor names its handler and the type it is declared for, and runs it only for an event of that type.
    public async Task Executor_RunsItsHandlerOnlyForItsEventType()
    {
        var publisher = new LevelCountingPublisher();
        var mediator = Mediator(
            ServiceLifetime.Singleton, static x => x.AddHandler<LevelDerivedHandler>(), new LevelLog(),
            services => services.AddSingleton<IEventPublisher>(publisher));

        await mediator.Publish(new LevelDerived());

        var executor = Assert.Single(publisher.Last);
        Assert.Equal(typeof(LevelDerived), executor.EventType);
        Assert.IsType<LevelDerivedHandler>(executor.Handler);
        Assert.Throws<InvalidCastException>(() => { _ = executor.Invoke(new LevelOrphan(), CancellationToken.None); });
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    // A mediator asks the container once for a publisher registered with its own lifetime, cold or warm, whether
    // an event has one level or several. One registered with a shorter lifetime is resolved for each publish
    // instead (see ServiceRetentionTests).
    public async Task ConfiguredPublisher_IsResolvedOncePerMediator(ServiceLifetime lifetime)
    {
        var resolutions = 0;
        var mediator = Mediator(
            lifetime,
            static x => x.AddHandler<LevelDerivedHandler>().AddHandler<LevelBaseHandler>(),
            new LevelLog(),
            services => services.Add(new ServiceDescriptor(typeof(IEventPublisher), _ =>
            {
                Interlocked.Increment(ref resolutions);
                return new LevelCountingPublisher();
            }, lifetime)));

        for (var round = 0; round < Rounds; round++)
        {
            await mediator.Publish(new LevelDerived());
            await mediator.Publish(new LevelBase());
        }

        Assert.Equal(1, resolutions);
    }

    [Fact]
    public async Task Middleware_WrapsTheHandlersOfItsOwnLevel()
    {
        var log = new LevelLog();
        var mediator = Levels(ServiceLifetime.Singleton, log, static x => x.AddMiddleware<LevelBaseMiddleware>());
        await mediator.Publish(new LevelDerived());

        Assert.Equal(["derived", "before:base", "base", "audited", "tracked"], log.Take());
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    public async Task ValueTypeEvent_ReachesItsInterfaceLevel(ServiceLifetime lifetime)
    {
        var log = new LevelLog();
        var mediator = Levels(lifetime, log, static x => x.AddHandler<LevelPingHandler>());
        for (var round = 0; round < Rounds; round++)
        {
            await mediator.Publish(new LevelPing());

            Assert.Equal(["ping", "audited"], log.Take());

            await mediator.Publish((object)new LevelPing());

            Assert.Equal(["ping", "audited"], log.Take());
        }
    }

    [Fact]
    // An event with no level, since nothing declares a handler for any type it is, publishes to no handlers from every
    // entry point, as one whose levels have no handler registered does.
    public async Task EventWithoutADeclaredLevel_PublishesToNoneFromEveryEntryPoint()
    {
        var log = new LevelLog();
        var mediator = Levels(ServiceLifetime.Singleton, log);

        await mediator.Publish(new LevelOrphan());
        await mediator.Publish<IEvent>(new LevelOrphan());
        await mediator.Publish((object)new LevelOrphan());

        Assert.Empty(log.Take());
    }
}

/// <summary>What the level handlers saw, and what they are told to do.</summary>
public sealed class LevelLog
{
    private readonly List<string> _entries = [];

    // Held until the test releases it, so a suspended handler is still pending however busy the machine is.
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _suspensions;

    /// <summary>A handler of this name, or every handler for "*", throws before it returns, as one that is not async would.</summary>
    public string? FailOn { get; init; }

    /// <summary>A handler of this name, or every handler for "*", suspends before recording until <see cref="Release" />.</summary>
    public string? SuspendOn { get; init; }

    /// <summary>How many handlers have suspended so far.</summary>
    public int Suspensions => Volatile.Read(ref _suspensions);

    public void Release() => _release.SetResult();

    public ValueTask Record(string entry)
    {
        if (entry == FailOn || FailOn == "*") throw new InvalidOperationException(entry);
        if (entry == SuspendOn || SuspendOn == "*") return RecordLater(entry);

        Add(entry);

        return default;
    }

    private async ValueTask RecordLater(string entry)
    {
        Interlocked.Increment(ref _suspensions);
        await _release.Task;
        Add(entry);
    }

    private void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    public string[] Take()
    {
        lock (_entries)
        {
            string[] entries = [.. _entries];
            _entries.Clear();

            return entries;
        }
    }
}

public interface ILevelAudited : IEvent;

public interface ILevelTracked : IEvent;

public record LevelBase : IEvent;

public sealed record LevelDerived : LevelBase, ILevelAudited, ILevelTracked;

public readonly record struct LevelPing : ILevelAudited;

public sealed record LevelOrphan : IEvent;

public sealed class LevelBaseHandler(LevelLog log) : IEventHandler<LevelBase>
{
    public ValueTask Handle(LevelBase message, CancellationToken cancellationToken) => log.Record("base");
}

public sealed class LevelDerivedHandler(LevelLog log) : IEventHandler<LevelDerived>
{
    public ValueTask Handle(LevelDerived message, CancellationToken cancellationToken) => log.Record("derived");
}

public sealed class LevelAuditedHandler(LevelLog log) : IEventHandler<ILevelAudited>
{
    public ValueTask Handle(ILevelAudited message, CancellationToken cancellationToken) => log.Record("audited");
}

public sealed class LevelTrackedHandler(LevelLog log) : IEventHandler<ILevelTracked>
{
    public ValueTask Handle(ILevelTracked message, CancellationToken cancellationToken) => log.Record("tracked");
}

public sealed class LevelPingHandler(LevelLog log) : IEventHandler<LevelPing>
{
    public ValueTask Handle(LevelPing message, CancellationToken cancellationToken) => log.Record("ping");
}

public sealed class LevelBaseMiddleware(LevelLog log) : IMiddleware<LevelBase>
{
    public async ValueTask Handle(LevelBase message, MessageHandle<LevelBase> next, CancellationToken cancellationToken)
    {
        await log.Record("before:base");
        await next(message, cancellationToken);
    }
}

/// <summary>Records the level types of each call, then runs the handlers one at a time.</summary>
public sealed class LevelCountingPublisher : IEventPublisher
{
    public List<string> Calls { get; } = [];

    public IReadOnlyList<EventHandlerExecutor> Last { get; private set; } = [];

    public async ValueTask Publish(IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken)
    {
        Calls.Add(string.Join(" ", executors.Select(static x => x.EventType.Name)));
        Last = executors;
        foreach (var executor in executors) await executor.Invoke(message, cancellationToken);
    }
}

/// <summary>Starts every handler before awaiting any.</summary>
public sealed class StartingPublisher : IEventPublisher
{
    public async ValueTask Publish(IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken)
    {
        var running = executors.Select(x => x.Invoke(message, cancellationToken).AsTask()).ToArray();
        await Task.WhenAll(running);
    }
}