using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Fixtures;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Dispatch;

// Exercises the per-instance dispatch cache: cold slow-path population, warm Unsafe.As hits, the uncached (Transient /
// direct-ctor) bypass, and messages with no handler.
public sealed class MediatorCacheTests
{
    private static IServiceProvider Provider(Action<MediatorBuilder> configure, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddSingleton<TraceLog>();
        extra?.Invoke(services);
        services.AddMediator(configure);
        return services.BuildServiceProvider();
    }

    private static IMediator ScopedMediator(Action<MediatorBuilder> configure, Action<IServiceCollection>? extra = null) =>
        Provider(configure, extra).CreateScope().ServiceProvider.GetRequiredService<IMediator>();

    // ---- cold -> warm (cached) --------------------------------------------------------------------------------------

    [Fact]
    public async Task VoidRequest_ColdThenWarmScoped_HitsCachedDelegate()
    {
        var sp = Provider(x => x.AddHandler<VoidRequestHandler>());
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var counter = scope.ServiceProvider.GetRequiredService<Counter>();

        await mediator.Request(new VoidRequest()); // cold: slow-path populates the slot
        await mediator.Request(new VoidRequest()); // warm: Unsafe.As cached delegate

        Assert.Equal(2, counter.Value);
    }

    [Fact]
    public async Task ResponseRequest_ColdThenWarmScoped_HitsCachedDelegate()
    {
        var mediator = ScopedMediator(x => x.AddHandler<EchoRequestHandler>());
        Assert.Equal("echo:a", await mediator.Request<EchoRequest, string>(new EchoRequest("a")));
        Assert.Equal("echo:b", await mediator.Request<EchoRequest, string>(new EchoRequest("b"))); // warm
    }

    [Fact]
    public async Task VoidRequest_ColdThenWarmSingleton_HitsCachedDelegate()
    {
        var sp = Provider(x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<VoidRequestHandler>());
        var mediator = sp.GetRequiredService<IMediator>();
        var counter = sp.GetRequiredService<Counter>();

        await mediator.Request(new VoidRequest());
        await mediator.Request(new VoidRequest());

        Assert.Equal(2, counter.Value);
    }

    [Fact]
    public async Task Publish_ColdThenWarmParallelDefault_HitsCachedEntry()
    {
        var sp = Provider(x => x
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var log = scope.ServiceProvider.GetRequiredService<TraceLog>();

        await mediator.Publish(new TestEvent("a")); // cold -> slow
        await mediator.Publish(new TestEvent("b")); // warm -> ParallelPublishing via cached entry

        Assert.Equal(4, log.Entries.Count);
    }

    [Fact]
    public async Task Publish_ColdThenWarmSequentialDefault_HitsCachedEntry()
    {
        var sp = Provider(x => x
            .WithEventPublisher<SequentialPublisher>()
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var log = scope.ServiceProvider.GetRequiredService<TraceLog>();

        await mediator.Publish(new TestEvent("a"));
        await mediator.Publish(new TestEvent("b")); // warm: entry.Publisher is not ParallelPublisher

        Assert.Equal(["first:a", "second:a", "first:b", "second:b"], log.Entries);
    }

    // ---- override publisher (cached warm + null + uncached) ---------------------------------------------------------

    [Fact]
    public async Task PublishWithOverride_AfterWarmingSlot_UsesCachedEntryHandlers()
    {
        var mediator = ScopedMediator(x => x
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        await mediator.Publish(new TestEvent("warm")); // warm the slot first
        var tracking = new CountingPublisher();
        await mediator.Publish(new TestEvent("over"), tracking); // warm + override -> Unsafe.As entry

        Assert.True(tracking.Used);
    }

    [Fact]
    public async Task Publish_WarmMediator_HandsTheChainsToACustomDefaultPublisher()
    {
        var tracking = new CountingPublisher();
        var sp = Provider(
            x => x.AddHandler<FirstEventHandler>(),
            s => s.AddSingleton<IEventPublisher>(tracking));

        var mediator = new DefaultMediator(sp, 1);
        await mediator.Publish(new TestEvent("c"));

        Assert.True(tracking.Used);
        Assert.Contains("first:c", sp.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    public async Task Publish_WarmMediator_RunsTheParallelLoopForParallelPublisher()
    {
        var sp = Provider(x => x
            .WithEventPublisher<ParallelPublisher>()
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        var mediator = new DefaultMediator(sp, 1);
        await mediator.Publish(new TestEvent("p"));

        Assert.Equal(["first:p", "second:p"], sp.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    // A publish through an abstraction, or of an object, reaches the level of the runtime type without naming a
    // publisher, so once that level is cached its entry supplies the configured one.
    public async Task PublishThroughAnAbstraction_Warm_UsesCachedEntryWithConfiguredPublisher()
    {
        var tracking = new CountingPublisher();
        var sp = Provider(x => x.AddHandler<FirstEventHandler>(), s => s.AddSingleton<IEventPublisher>(tracking));
        var mediator = new DefaultMediator(sp, 1);

        await mediator.Publish<IEvent>(new TestEvent("a")); // cold: caches the TestEvent level
        await mediator.Publish<IEvent>(new TestEvent("b")); // warm
        await mediator.Publish((object)new TestEvent("c")); // warm

        Assert.Equal(3, tracking.Calls);
        Assert.Equal(["first:a", "first:b", "first:c"], sp.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    public async Task PublishWithNullOverride_DelegatesToDefault()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>());
        using var scope = sp.CreateScope();

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var log = scope.ServiceProvider.GetRequiredService<TraceLog>();
        await mediator.Publish(new TestEvent("n"), null);

        Assert.Contains("first:n", log.Entries);
    }

    [Fact]
    public async Task PublishWithOverride_Uncached_UsesTheOverride()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>());
        var mediator = new DefaultMediator(sp); // public ctor => uncached
        var tracking = new CountingPublisher();
        await mediator.Publish(new TestEvent("u"), tracking);

        Assert.True(tracking.Used);
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    // A publish that names its publisher never constructs the configured one, cold or warm, whether the event has one
    // level or several. A mediator caches an event's entry together with the configured publisher, so such publishes
    // cache nothing until a publish without a publisher has resolved it, and that publish then uses it as usual.
    public async Task PublishWithOverride_DoesNotConstructTheConfiguredPublisher(ServiceLifetime lifetime)
    {
        var constructed = 0;

        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<TraceLog>();
        services.AddSingleton(new LevelLog());
        services.Add(new ServiceDescriptor(typeof(IEventPublisher), _ =>
        {
            constructed++;
            return new SequentialPublisher();
        }, lifetime));

        services.AddMediator(x => x.WithLifetime(lifetime).AddHandler<FirstEventHandler>()
            .AddHandler<LevelDerivedHandler>().AddHandler<LevelBaseHandler>());

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        for (var round = 0; round < 10; round++)
        {
            await mediator.Publish(new TestEvent("given"), new SequentialPublisher());
            await mediator.Publish(new LevelDerived(), new LevelCountingPublisher());
        }

        Assert.Equal(0, constructed);

        await mediator.Publish(new TestEvent("configured"));

        Assert.Equal(1, constructed);
        Assert.Contains("first:configured", provider.GetRequiredService<TraceLog>().Entries);
    }

    // ---- uncached bypass (public ctor) ------------------------------------------------------------------------------

    [Fact]
    public async Task PublicCtor_UncachedVoidRequest_ResolvesHandlerEachCall()
    {
        var sp = Provider(x => x.AddHandler<VoidRequestHandler>());
        var mediator = new DefaultMediator(sp);
        var counter = sp.GetRequiredService<Counter>();
        await mediator.Request(new VoidRequest());

        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public async Task PublicCtor_UncachedResponseRequest_ResolvesHandlerEachCall()
    {
        var sp = Provider(x => x.AddHandler<EchoRequestHandler>());
        var mediator = new DefaultMediator(sp);
        Assert.Equal("echo:u", await mediator.Request<EchoRequest, string>(new EchoRequest("u")));
    }

    [Fact]
    public async Task PublicCtor_UncachedPublish_ResolvesHandlersEachCall()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>());
        var mediator = new DefaultMediator(sp);
        var log = sp.GetRequiredService<TraceLog>();
        await mediator.Publish(new TestEvent("u"));

        Assert.Contains("first:u", log.Entries);
    }

    // ---- no handler for the message ---------------------------------------------------------------------------------

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [Fact]
    public void VoidRequest_NoHandler_ThrowsNamingTheMessage()
    {
        var mediator = ScopedMediator(x => x.AddHandler<VoidRequestHandler>());
        var exception = Assert.Throws<InvalidOperationException>(() => { _ = mediator.Request(new OrphanVoidRequest()); });
        Assert.Contains(nameof(OrphanVoidRequest), exception.Message);
    }

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [Fact]
    public void ResponseRequest_NoHandler_ThrowsNamingTheMessage()
    {
        var mediator = ScopedMediator(x => x.AddHandler<EchoRequestHandler>());
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = mediator.Request<OrphanResponseRequest, int>(new OrphanResponseRequest());
        });

        Assert.Contains(nameof(OrphanResponseRequest), exception.Message);
    }

    [Fact]
    // An event has any number of handlers, none included, so one without a handler publishes to none.
    public async Task Event_NoHandler_PublishesToNone()
    {
        var mediator = ScopedMediator(x => x.AddHandler<FirstEventHandler>());
        await mediator.Publish(new OrphanEvent());
        await mediator.Publish(new OrphanEvent());
    }

    // ---- fakes / orphan types ---------------------------------------------------------------------------------------

    private sealed record OrphanVoidRequest : IRequest;

    private sealed record OrphanResponseRequest : IRequest<int>;

    private sealed record OrphanEvent : IEvent;

    private sealed class CountingPublisher : IEventPublisher
    {
        public int Calls { get; private set; }

        public bool Used => Calls > 0;

        public ValueTask Publish(IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken)
        {
            Calls++;
            var tasks = new Task[executors.Count];
            for (var i = 0; i < executors.Count; i++) tasks[i] = executors[i].Invoke(message, cancellationToken).AsTask();

            return new ValueTask(Task.WhenAll(tasks));
        }
    }
}