using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Dispatch;

// Routes are created on the first dispatch of a runtime type and kept in process-wide tables, so which path a dispatch
// takes depends on what already ran. Every test that pins a first dispatch therefore owns its message types: sharing
// one with another test would make the path depend on execution order.
public sealed class MessageRoutesTests
{
    private static IMediator Mediator(Action<MediatorBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddMediator(configure);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        List<T> items = [];
        await foreach (var item in stream) items.Add(item);

        return items;
    }

    // ---- table ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Table_FindsNothing_ForATypeNeverAdded()
    {
        var table = new RouteTable<Route>();
        Assert.Null(table.Find(typeof(Route)));
    }

    [Fact]
    public void Table_KeepsTheFirstRoute_WhenATypeIsAddedTwice()
    {
        var table = new RouteTable<Route>();
        var first = new Route();

        table.Add(typeof(Route), first);
        table.Add(typeof(Route), new Route());
        Assert.Same(first, table.Find(typeof(Route)));
    }

    [Fact]
    public void Table_CollisionsAndGrowth_KeepEveryRoute()
    {
        // Far past the initial 64 buckets at a load factor of one half, so probing and rehashing both run.
        var types = typeof(object).Assembly.GetExportedTypes().Take(1000).ToArray();
        var table = new RouteTable<Route>();
        var routes = types.ToDictionary(static x => x, static _ => new Route());

        foreach (var (type, route) in routes) table.Add(type, route);
        foreach (var (type, route) in routes) Assert.Same(route, table.Find(type));

        Assert.Null(table.Find(typeof(Route)));
    }

    // ---- first dispatch, by entry point -----------------------------------------------------------------------------

    [Fact]
    public async Task InterfaceTypedRequest_MemoizesItsRoute_OnFirstDispatch()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteByInterfaceHandler>());

        Assert.Null(MessageRoutes.Current.Requests.Find(typeof(RouteByInterface)));
        Assert.Equal(2, await mediator.Request(new RouteByInterface(1)));
        Assert.NotNull(MessageRoutes.Current.Requests.Find(typeof(RouteByInterface)));
        Assert.Equal(3, await mediator.Request(new RouteByInterface(2)));
    }

    [Fact]
    public async Task ObjectRequest_MemoizesItsRoute_OnFirstDispatch()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteByObjectHandler>());

        Assert.Null(MessageRoutes.Current.Requests.Find(typeof(RouteByObject)));
        Assert.Equal(2, await mediator.Request((object)new RouteByObject(1)));
        Assert.NotNull(MessageRoutes.Current.Requests.Find(typeof(RouteByObject)));
        Assert.Equal(3, await mediator.Request((object)new RouteByObject(2)));
    }

    [Fact]
    public async Task ObjectEvent_MemoizesItsRoute_OnFirstDispatch()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteEventHandler>());

        Assert.Null(MessageRoutes.Current.Events.Find(typeof(RouteEvent)));

        await mediator.Publish((object)new RouteEvent());

        Assert.NotNull(MessageRoutes.Current.Events.Find(typeof(RouteEvent)));

        await mediator.Publish((object)new RouteEvent());
    }

    [Fact]
    public async Task InterfaceTypedStream_MemoizesItsRoute_OnFirstDispatch()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteStreamByInterfaceHandler>());

        Assert.Null(MessageRoutes.Current.Streams.Find(typeof(RouteStreamByInterface)));
        Assert.Equal([1, 2], await Collect(mediator.RequestStream(new RouteStreamByInterface())));
        Assert.NotNull(MessageRoutes.Current.Streams.Find(typeof(RouteStreamByInterface)));
        Assert.Equal([1, 2], await Collect(mediator.RequestStream(new RouteStreamByInterface())));
    }

    [Fact]
    public async Task ObjectStream_MemoizesItsRoute_OnFirstDispatch()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteStreamByObjectHandler>());

        Assert.Null(MessageRoutes.Current.Streams.Find(typeof(RouteStreamByObject)));
        Assert.Equal([1, 2], await Collect(mediator.RequestStream((object)new RouteStreamByObject())));
        Assert.NotNull(MessageRoutes.Current.Streams.Find(typeof(RouteStreamByObject)));
        Assert.Equal([1, 2], await Collect(mediator.RequestStream((object)new RouteStreamByObject())));
    }

    // ---- shapes the walk and the casts have to handle ---------------------------------------------------------------

    [Fact]
    public async Task DerivedMessage_Dispatched_SharesBaseRouteInWalkedTable()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteBaseHandler>());
        Assert.Equal(1, await mediator.Request((object)new RouteBase(1)));

        var baseRoute = MessageRoutes.Current.Requests.Find(typeof(RouteBase));

        Assert.Null(MessageRoutes.WalkedRequests.Find(typeof(RouteDerived)));
        Assert.Equal(2, await mediator.Request((object)new RouteDerived(2)));
        Assert.Same(baseRoute, MessageRoutes.WalkedRequests.Find(typeof(RouteDerived)));
        Assert.Null(MessageRoutes.Current.Requests.Find(typeof(RouteDerived)));
    }

    [Fact]
    // A module initializer can run after a request has walked to its base's route, from an assembly that declares the
    // request's own type. The walk is forgotten then, so every entry point reaches the nearer handler. The resolvers
    // below stand in for that assembly. Adding them replaces the walked tables that other tests here inspect, which is
    // safe because the tests of one class never run in parallel.
    public async Task AssemblyInitializedAfterAWalk_TakesOverTheTypesItDeclares()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<LateDeclared, string>, LateDeclaredHandler<LateDeclared>>();
        services.AddTransient<IStreamRequestHandler<LateDeclaredStream, string>, LateDeclaredStreamHandler<LateDeclaredStream>>();
        services.AddMediator(static x => x.AddHandler<LateDeclaredBaseHandler>().AddHandler<LateDeclaredStreamBaseHandler>());

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        Assert.Equal("base", await mediator.Request(new LateDeclared()));
        Assert.Equal("base", await mediator.Request<LateDeclared, string>(new LateDeclared()));
        Assert.Equal("base", await mediator.Request((object)new LateDeclared()));
        Assert.Equal(["base"], await Collect(mediator.RequestStream(new LateDeclaredStream())));
        Assert.Equal(["base"], await Collect(mediator.RequestStream((object)new LateDeclaredStream())));

        MessageRoutes.AddResolvers(
            static type => type == typeof(LateDeclared) ? new ResponseRequestRoute<LateDeclared, string>() : null,
            static _ => null,
            null,
            static type => type == typeof(LateDeclaredStream) ? new StreamRoute<LateDeclaredStream, string>() : null);

        Assert.Equal("late", await mediator.Request(new LateDeclared()));
        Assert.Equal("late", await mediator.Request<LateDeclared, string>(new LateDeclared()));
        Assert.Equal("late", await mediator.Request((object)new LateDeclared()));
        Assert.Equal(["late"], await Collect(mediator.RequestStream(new LateDeclaredStream())));
        Assert.Equal(["late"], await Collect(mediator.RequestStream<LateDeclaredStream, string>(new LateDeclaredStream())));
        Assert.Equal(["late"], await Collect(mediator.RequestStream((object)new LateDeclaredStream())));
    }

    [Fact]
    // A typed dispatch remembers that its message type declares a handler only when it finds that type's own route. An
    // exact instance of a type that declares none walks to its base's route every time, here past a handler registered
    // by hand for the type itself, which a type remembered from the walk would reach from the second dispatch on.
    public async Task ExactInstanceOfAnUndeclaredType_WalksToItsBaseEveryTime()
    {
        var log = new RouteLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddTransient<IRequestHandler<Walked, string>, LateDeclaredHandler<Walked>>();
        services.AddTransient<IRequestHandler<WalkedVoid>, WalkedVoidHandler<WalkedVoid>>();
        services.AddTransient<IStreamRequestHandler<WalkedStream, string>, LateDeclaredStreamHandler<WalkedStream>>();
        services.AddMediator(static x => x.AddHandler<WalkedBaseHandler>().AddHandler<WalkedVoidBaseHandler>()
            .AddHandler<WalkedStreamBaseHandler>());

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        for (var round = 0; round < 3; round++)
        {
            Assert.Equal("base", await mediator.Request<Walked, string>(new Walked()));

            await mediator.Request(new WalkedVoid());
            Assert.Equal(["base"], await Collect(mediator.RequestStream<WalkedStream, string>(new WalkedStream())));
        }

        Assert.Equal(["base", "base", "base"], log.Entries);
    }

    [Fact]
    // Such an assembly can also declare a level of an event already published, with handlers for an interface or a base
    // class the event is. A typed publish had found the event reaching its own level alone, so a mediator that caches
    // has to find out that it no longer does, as the publish of an object does by looking up the levels again.
    public async Task AssemblyInitializedAfterAPublish_AddsTheLevelItDeclares()
    {
        var log = new RouteLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddTransient<IEventHandler<ILateLevel>, LateLevelHandler<ILateLevel>>();
        services.AddMediator(static x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<LateLevelEventHandler>());

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        await mediator.Publish(new LateLevelEvent());
        await mediator.Publish(new LateLevelEvent(), new SequentialPublisher());

        Assert.Equal(["own", "own"], log.Entries);

        MessageRoutes.AddResolvers(
            static _ => null,
            static type => type == typeof(ILateLevel) ? new EventRoute<ILateLevel>() : null,
            static (message, types) =>
            {
                if (message is ILateLevel) types.Add(typeof(ILateLevel));
            },
            static _ => null);

        log.Entries.Clear();

        await mediator.Publish(new LateLevelEvent());
        await mediator.Publish(new LateLevelEvent(), new SequentialPublisher());
        await mediator.Publish((object)new LateLevelEvent());

        Assert.Equal(["own", "late", "own", "late", "own", "late"], log.Entries);
    }

    [Fact]
    // An event stays complete until an assembly adds resolvers. A typed publish records it with the routing that found
    // its levels, so one that raced an assembly adding them names a replaced routing, which is ignored.
    public void CompleteEvent_LastsUntilAnAssemblyAddsResolvers()
    {
        var routing = MessageRoutes.Current;
        MessageRoutes.FoundComplete<ReplacedLevelsEvent>(routing);
        MessageRoutes.FoundComplete<ReplacedLevelsEvent>(routing);
        Assert.True(MessageRoutes.IsComplete<ReplacedLevelsEvent>());

        MessageRoutes.AddResolvers(static _ => null, static _ => null, null, static _ => null);
        Assert.False(MessageRoutes.IsComplete<ReplacedLevelsEvent>());

        MessageRoutes.FoundComplete<ReplacedLevelsEvent>(routing);
        Assert.False(MessageRoutes.IsComplete<ReplacedLevelsEvent>());
    }

    // The generator declares a route for every handler it sees, whether or not a container registers it. A derived
    // message then walks to its base's route, which must end in an exception: going back to the runtime type overflows
    // the stack, and that takes the process down. A regression here crashes the test host instead of failing one test.

    [Fact]
    public async Task DerivedRequest_WithAnUnregisteredBaseHandler_Throws()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteBaseHandler>());
        var inferred = await Assert.ThrowsAsync<InvalidOperationException>(async () => await mediator.Request(new UnregisteredDerived()));

        var typed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await mediator.Request<UnregisteredBase, int>(new UnregisteredDerived()));

        Assert.Contains(nameof(UnregisteredBase), inferred.Message);
        Assert.Contains(nameof(UnregisteredBase), typed.Message);
    }

    [Fact]
    public async Task DerivedVoidRequest_WithAnUnregisteredBaseHandler_Throws()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteBaseHandler>());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await mediator.Request<UnregisteredVoidBase>(new UnregisteredVoidDerived()));

        Assert.Contains(nameof(UnregisteredVoidBase), exception.Message);
    }

    [Fact]
    public void DerivedStream_WithAnUnregisteredBaseHandler_Throws()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteBaseHandler>());
        var inferred = Assert.Throws<InvalidOperationException>(() => mediator.RequestStream(new UnregisteredStreamDerived()));

        var typed = Assert.Throws<InvalidOperationException>(() =>
            mediator.RequestStream<UnregisteredStreamBase, int>(new UnregisteredStreamDerived()));

        Assert.Contains(nameof(UnregisteredStreamBase), inferred.Message);
        Assert.Contains(nameof(UnregisteredStreamBase), typed.Message);
    }

    [Fact]
    public async Task EventWithNoHandlerHere_ReachesItsPublisher_WhenSomeAssemblyDeclaresOne()
    {
        var publisher = new EmptyListPublisher();
        var services = new ServiceCollection();
        services.AddSingleton<IEventPublisher>(publisher);
        services.AddMediator(static x => x.AddHandler<RouteBaseHandler>());

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        await mediator.Publish(new UnregisteredEvent());

        Assert.Equal(1, publisher.Calls);
    }

    [Fact]
    public async Task DerivedEvent_WithAnUnregisteredBaseHandler_ReachesNobody()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteBaseHandler>());
        await mediator.Publish<UnregisteredEventBase>(new UnregisteredEventDerived());
        await mediator.Publish((object)new UnregisteredEventDerived());
        await mediator.Publish<UnregisteredEventBase>(new UnregisteredEventDerived(), new EmptyListPublisher());
    }

    [Fact]
    public async Task ValueTypeMessage_IsUnboxed_ThroughTheRoute()
    {
        var mediator = Mediator(static x => x.AddHandler<RouteStructHandler>());
        IRequest<int> message = new RouteStruct(4);
        Assert.Equal(4, await mediator.Request(message));
        Assert.Equal(4, await mediator.Request((object)message));
    }

    private sealed class Route;

    private sealed class EmptyListPublisher : IEventPublisher
    {
        public int Calls { get; private set; }

        public ValueTask Publish(IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken)
        {
            Assert.Empty(executors);

            Calls++;

            return default;
        }
    }
}

public sealed record RouteByInterface(int Value) : IRequest<int>;

public sealed class RouteByInterfaceHandler : IRequestHandler<RouteByInterface, int>
{
    public ValueTask<int> Handle(RouteByInterface message, CancellationToken cancellationToken) => new(message.Value + 1);
}

public sealed record RouteByObject(int Value) : IRequest<int>;

public sealed class RouteByObjectHandler : IRequestHandler<RouteByObject, int>
{
    public ValueTask<int> Handle(RouteByObject message, CancellationToken cancellationToken) => new(message.Value + 1);
}

public sealed record RouteEvent : IEvent;

public sealed class RouteEventHandler : IEventHandler<RouteEvent>
{
    public ValueTask Handle(RouteEvent message, CancellationToken cancellationToken) => default;
}

public sealed record RouteStreamByInterface : IStreamRequest<int>;

public sealed class RouteStreamByInterfaceHandler : IStreamRequestHandler<RouteStreamByInterface, int>
{
    public async IAsyncEnumerable<int> Handle(RouteStreamByInterface message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        yield return 2;
    }
}

public sealed record RouteStreamByObject : IStreamRequest<int>;

public sealed class RouteStreamByObjectHandler : IStreamRequestHandler<RouteStreamByObject, int>
{
    public async IAsyncEnumerable<int> Handle(RouteStreamByObject message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        yield return 2;
    }
}

public record UnregisteredBase : IRequest<int>;

public sealed record UnregisteredDerived : UnregisteredBase;

public sealed class UnregisteredBaseHandler : IRequestHandler<UnregisteredBase, int>
{
    public ValueTask<int> Handle(UnregisteredBase message, CancellationToken cancellationToken) => new(1);
}

public sealed record UnregisteredEvent : IEvent;

public sealed class UnregisteredEventHandler : IEventHandler<UnregisteredEvent>
{
    public ValueTask Handle(UnregisteredEvent message, CancellationToken cancellationToken) => default;
}

public record UnregisteredEventBase : IEvent;

public sealed record UnregisteredEventDerived : UnregisteredEventBase;

public sealed class UnregisteredEventBaseHandler : IEventHandler<UnregisteredEventBase>
{
    public ValueTask Handle(UnregisteredEventBase message, CancellationToken cancellationToken) => default;
}

public record UnregisteredVoidBase : IRequest;

public sealed record UnregisteredVoidDerived : UnregisteredVoidBase;

public sealed class UnregisteredVoidBaseHandler : IRequestHandler<UnregisteredVoidBase>
{
    public ValueTask Handle(UnregisteredVoidBase message, CancellationToken cancellationToken) => default;
}

public record UnregisteredStreamBase : IStreamRequest<int>;

public sealed record UnregisteredStreamDerived : UnregisteredStreamBase;

public sealed class UnregisteredStreamBaseHandler : IStreamRequestHandler<UnregisteredStreamBase, int>
{
    public IAsyncEnumerable<int> Handle(UnregisteredStreamBase message, CancellationToken cancellationToken) =>
        AsyncEnumerable.Empty<int>();
}

public record RouteBase(int Value) : IRequest<int>;

public sealed record RouteDerived(int Value) : RouteBase(Value);

public sealed class RouteBaseHandler : IRequestHandler<RouteBase, int>
{
    public ValueTask<int> Handle(RouteBase message, CancellationToken cancellationToken) => new(message.Value);
}

public record LateDeclaredBase : IRequest<string>;

public sealed record LateDeclared : LateDeclaredBase;

public sealed class LateDeclaredBaseHandler : IRequestHandler<LateDeclaredBase, string>
{
    public ValueTask<string> Handle(LateDeclaredBase message, CancellationToken cancellationToken) => new("base");
}

// Generic, so the generator declares no route for what it handles, as if it sat in an assembly not yet initialized.
public sealed class LateDeclaredHandler<TRequest> : IRequestHandler<TRequest, string> where TRequest : IRequest<string>
{
    public ValueTask<string> Handle(TRequest message, CancellationToken cancellationToken) => new("late");
}

public record LateDeclaredStreamBase : IStreamRequest<string>;

public sealed record LateDeclaredStream : LateDeclaredStreamBase;

public sealed class LateDeclaredStreamBaseHandler : IStreamRequestHandler<LateDeclaredStreamBase, string>
{
    public IAsyncEnumerable<string> Handle(LateDeclaredStreamBase message, CancellationToken cancellationToken) =>
        new[] { "base" }.ToAsyncEnumerable();
}

public sealed class LateDeclaredStreamHandler<TRequest> : IStreamRequestHandler<TRequest, string> where TRequest : IStreamRequest<string>
{
    public IAsyncEnumerable<string> Handle(TRequest message, CancellationToken cancellationToken) =>
        new[] { "late" }.ToAsyncEnumerable();
}

public record WalkedBase : IRequest<string>;

public sealed record Walked : WalkedBase;

public sealed class WalkedBaseHandler : IRequestHandler<WalkedBase, string>
{
    public ValueTask<string> Handle(WalkedBase message, CancellationToken cancellationToken) => new("base");
}

public sealed class RouteLog
{
    public List<string> Entries { get; } = [];
}

public record WalkedVoidBase : IRequest;

public sealed record WalkedVoid : WalkedVoidBase;

public sealed class WalkedVoidBaseHandler(RouteLog log) : IRequestHandler<WalkedVoidBase>
{
    public ValueTask Handle(WalkedVoidBase message, CancellationToken cancellationToken)
    {
        log.Entries.Add("base");
        return default;
    }
}

// Generic, so the generator declares no route for what it handles.
public sealed class WalkedVoidHandler<TRequest>(RouteLog log) : IRequestHandler<TRequest> where TRequest : IRequest
{
    public ValueTask Handle(TRequest message, CancellationToken cancellationToken)
    {
        log.Entries.Add("own");
        return default;
    }
}

public record WalkedStreamBase : IStreamRequest<string>;

public sealed record WalkedStream : WalkedStreamBase;

public sealed class WalkedStreamBaseHandler : IStreamRequestHandler<WalkedStreamBase, string>
{
    public IAsyncEnumerable<string> Handle(WalkedStreamBase message, CancellationToken cancellationToken) =>
        new[] { "base" }.ToAsyncEnumerable();
}

public interface ILateLevel : IEvent;

public sealed record LateLevelEvent : ILateLevel;

public sealed class LateLevelEventHandler(RouteLog log) : IEventHandler<LateLevelEvent>
{
    public ValueTask Handle(LateLevelEvent message, CancellationToken cancellationToken)
    {
        log.Entries.Add("own");
        return default;
    }
}

public sealed record ReplacedLevelsEvent : IEvent;

// Generic, so the generator declares no level for what it handles, as LateDeclaredHandler does for a request.
public sealed class LateLevelHandler<TEvent>(RouteLog log) : IEventHandler<TEvent> where TEvent : IEvent
{
    public ValueTask Handle(TEvent message, CancellationToken cancellationToken)
    {
        log.Entries.Add("late");
        return default;
    }
}

public readonly record struct RouteStruct(int Value) : IRequest<int>;

public sealed class RouteStructHandler : IRequestHandler<RouteStruct, int>
{
    public ValueTask<int> Handle(RouteStruct message, CancellationToken cancellationToken) => new(message.Value);
}