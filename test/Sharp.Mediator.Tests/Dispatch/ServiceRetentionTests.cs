using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Dispatch;

// What a mediator keeps beyond the dispatch that resolved it, and what it resolves for each dispatch instead: it keeps
// a chain, and its publisher, only when every service in it lives at least as long as the mediator (see
// ServiceRetention). These tests count the instances that dispatches meet, which the tests of the cache's structure, in
// MediatorCacheTests and MediatorTypedCacheTests, do not look at: a cache that keeps and finds every chain it should
// can still keep, and share, an instance whose registration says it must not.
public sealed class ServiceRetentionTests
{
    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    // A mediator keeps a message's chain, and its publisher, for as long as it lives. A handler, middleware or
    // publisher registered with a shorter lifetime than the mediator's is resolved for each dispatch instead, however
    // warm the mediator is, including one registered after AddMediator. A cached mediator used to keep the instance its
    // chain was built with, so a Transient handler became one instance per Scoped mediator from its eighth dispatch on.
    public async Task ServicesRegisteredShorterThanTheMediator_AreResolvedForEachDispatch(ServiceLifetime lifetime)
    {
        var log = new InstanceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddTransient<IRequestHandler<LifetimePing, int>, LifetimePingHandler>();
        services.AddTransient<IMiddleware<LifetimeCommand>, LifetimeCommandMiddleware>();
        services.AddTransient<IEventHandler<LifetimeNotice>, LifetimeNoticeHandler>();
        services.AddTransient(typeof(IStreamMiddleware<,>), typeof(LifetimeStreamMiddleware<,>));
        services.AddTransient<IEventPublisher>(_ =>
        {
            log.Saw("publisher", log.Next());
            return new SequentialPublisher();
        });

        services.AddMediator(x => x.WithLifetime(lifetime).AddHandler<LifetimeCommandHandler>()
            .AddHandler<LifetimeTailHandler>().AddHandler<LifetimeControlHandler>());

        services.AddTransient<IRequestHandler<LifetimeLate, int>, LifetimeLateHandler>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        for (var round = 0; round < 12; round++)
        {
            await mediator.Request<LifetimePing, int>(new LifetimePing());
            await mediator.Request(new LifetimeCommand());
            await mediator.Publish(new LifetimeNotice());
            await foreach (var _ in mediator.RequestStream<LifetimeTail, int>(new LifetimeTail()))
            {
            }

            await mediator.Request<LifetimeLate, int>(new LifetimeLate());
            await mediator.Request<LifetimeControl, int>(new LifetimeControl());
        }

        Assert.Equal(12, log.Distinct("ping handler"));
        Assert.Equal(12, log.Distinct("command middleware"));
        Assert.Equal(12, log.Distinct("notice handler"));
        Assert.Equal(12, log.Distinct("stream middleware"));
        Assert.Equal(12, log.Distinct("late handler"));
        Assert.Equal(12, log.Distinct("publisher"));

        // A chain of services that live as long as the mediator is still kept.
        var cache = (DefaultMediator)mediator;
        Assert.NotNull(cache.Lookup(RequestSlot<LifetimeControl, int>.Index));
        Assert.Null(cache.Lookup(RequestSlot<LifetimePing, int>.Index));
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    // A publisher other than the sequential one receives every level's executors. The mediator keeps those of a
    // level whose handlers live as long as it does, and builds those of a level with a shorter-lived handler
    // for each publish.
    public async Task ShorterLivedHandlerOfOneLevel_IsResolvedForEachPublishThroughACustomPublisher(ServiceLifetime lifetime)
    {
        var log = new InstanceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddTransient<IEventHandler<LifetimeLevel>, LifetimeLevelHandler>();
        services.AddMediator(x => x.WithLifetime(lifetime).WithEventPublisher<LevelCountingPublisher>()
            .AddHandler<LifetimeLevelBaseHandler>());

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        for (var round = 0; round < 12; round++) await mediator.Publish(new LifetimeLevel());

        Assert.Equal(12, log.Distinct("level handler"));
        Assert.Equal(12, log.Count("base level handler"));
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    // Two dispatches inside a Transient handler at once, on a mediator that caches, each have an instance of their own.
    // The handler waits at a gate that the test opens only once both calls are inside it, so they overlap however the
    // machine schedules them. A mediator that kept the chain would hand both the instance it was built with, which two
    // calls would then use at the same time.
    public async Task OverlappingDispatches_ToAShorterLivedHandler_HaveAnInstanceEach(ServiceLifetime lifetime)
    {
        var log = new InstanceLog();
        var gate = new OverlapGate();
        var services = new ServiceCollection();
        services.AddSingleton(log).AddSingleton(gate);
        services.AddTransient<IRequestHandler<LifetimeOverlap, int>, LifetimeOverlapHandler>();
        services.AddMediator(x => x.WithLifetime(lifetime).AddHandler<LifetimeControlHandler>());

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        for (var round = 0; round < 8; round++) await mediator.Request<LifetimeControl, int>(new LifetimeControl());

        Assert.NotNull(((DefaultMediator)mediator).Lookup(RequestSlot<LifetimeControl, int>.Index));

        var first = mediator.Request<LifetimeOverlap, int>(new LifetimeOverlap());
        var second = mediator.Request<LifetimeOverlap, int>(new LifetimeOverlap());

        Assert.Equal(2, gate.Inside);

        gate.Open();

        Assert.NotEqual(await first, await second);
    }
}

/// <summary>Numbers the instances of the lifetime fakes and records which ones each dispatch reached.</summary>
public sealed class InstanceLog
{
    private readonly Dictionary<string, List<int>> _seen = [];
    private int _next;

    public int Next() => Interlocked.Increment(ref _next);

    public void Saw(string component, int instance)
    {
        lock (_seen)
        {
            if (!_seen.TryGetValue(component, out var instances)) _seen[component] = instances = [];

            instances.Add(instance);
        }
    }

    public int Distinct(string component)
    {
        lock (_seen)
        {
            return _seen[component].Distinct().Count();
        }
    }

    public int Count(string component)
    {
        lock (_seen)
        {
            return _seen[component].Count;
        }
    }
}

public sealed record LifetimePing : IRequest<int>;

public sealed class LifetimePingHandler(InstanceLog log) : IRequestHandler<LifetimePing, int>
{
    private readonly int _instance = log.Next();

    public ValueTask<int> Handle(LifetimePing message, CancellationToken cancellationToken)
    {
        log.Saw("ping handler", _instance);
        return ValueTask.FromResult(_instance);
    }
}

public sealed record LifetimeCommand : IRequest;

public sealed class LifetimeCommandHandler : IRequestHandler<LifetimeCommand>
{
    public ValueTask Handle(LifetimeCommand message, CancellationToken cancellationToken) => default;
}

public sealed class LifetimeCommandMiddleware(InstanceLog log) : IMiddleware<LifetimeCommand>
{
    private readonly int _instance = log.Next();

    public ValueTask Handle(LifetimeCommand message, MessageHandle<LifetimeCommand> next, CancellationToken cancellationToken)
    {
        log.Saw("command middleware", _instance);
        return next(message, cancellationToken);
    }
}

public sealed record LifetimeNotice : IEvent;

public sealed class LifetimeNoticeHandler(InstanceLog log) : IEventHandler<LifetimeNotice>
{
    private readonly int _instance = log.Next();

    public ValueTask Handle(LifetimeNotice message, CancellationToken cancellationToken)
    {
        log.Saw("notice handler", _instance);
        return default;
    }
}

public sealed record LifetimeTail : IStreamRequest<int>;

public sealed class LifetimeTailHandler : IStreamRequestHandler<LifetimeTail, int>
{
    private static readonly int[] SourceArray = [1];

    public IAsyncEnumerable<int> Handle(LifetimeTail message, CancellationToken cancellationToken) => SourceArray.ToAsyncEnumerable();
}

// Registered by hand as an open generic, so it applies to every stream of the container that registers it.
public sealed class LifetimeStreamMiddleware<TRequest, TResponse>(InstanceLog log)
    : IStreamMiddleware<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    private readonly int _instance = log.Next();

    public IAsyncEnumerable<TResponse> Handle(TRequest message, StreamMessageHandle<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        log.Saw("stream middleware", _instance);
        return next(message, cancellationToken);
    }
}

public sealed record LifetimeLate : IRequest<int>;

public sealed class LifetimeLateHandler(InstanceLog log) : IRequestHandler<LifetimeLate, int>
{
    private readonly int _instance = log.Next();

    public ValueTask<int> Handle(LifetimeLate message, CancellationToken cancellationToken)
    {
        log.Saw("late handler", _instance);
        return ValueTask.FromResult(_instance);
    }
}

public sealed record LifetimeOverlap : IRequest<int>;

public sealed class LifetimeOverlapHandler(InstanceLog log, OverlapGate gate) : IRequestHandler<LifetimeOverlap, int>
{
    private readonly int _instance = log.Next();

    public async ValueTask<int> Handle(LifetimeOverlap message, CancellationToken cancellationToken)
    {
        await gate.Enter();
        return _instance;
    }
}

/// <summary>Holds every call that enters it until the test opens it, and counts them.</summary>
public sealed class OverlapGate
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inside;

    public int Inside => Volatile.Read(ref _inside);

    public Task Enter()
    {
        Interlocked.Increment(ref _inside);
        return _open.Task;
    }

    public void Open() => _open.SetResult();
}

public sealed record LifetimeControl : IRequest<int>;

public sealed class LifetimeControlHandler : IRequestHandler<LifetimeControl, int>
{
    public ValueTask<int> Handle(LifetimeControl message, CancellationToken cancellationToken) => ValueTask.FromResult(0);
}

public record LifetimeLevelBase : IEvent;

public sealed record LifetimeLevel : LifetimeLevelBase;

public sealed class LifetimeLevelHandler(InstanceLog log) : IEventHandler<LifetimeLevel>
{
    private readonly int _instance = log.Next();

    public ValueTask Handle(LifetimeLevel message, CancellationToken cancellationToken)
    {
        log.Saw("level handler", _instance);
        return default;
    }
}

public sealed class LifetimeLevelBaseHandler(InstanceLog log) : IEventHandler<LifetimeLevelBase>
{
    private readonly int _instance = log.Next();

    public ValueTask Handle(LifetimeLevelBase message, CancellationToken cancellationToken)
    {
        log.Saw("base level handler", _instance);
        return default;
    }
}