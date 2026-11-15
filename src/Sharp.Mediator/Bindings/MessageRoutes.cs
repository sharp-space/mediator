using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sharp.Mediator.Bindings;

// Dispatch by runtime type. A generated module initializer contributes one resolver per assembly rather than a route
// per message: a route is created the first time its declared type is dispatched, and the route a runtime type walks to
// is remembered for that type, which makes the base-class walk a one-time cost as well. The routes are process-wide
// because a module initializer can run at any point, and when one does, a routing of every assembly's resolvers
// replaces the one that was walked: the new assembly can declare a type nearer to a runtime type than the one a walk
// found, or another level of an event. A route names the declared type whose handler runs and calls the mediator's
// dispatch for exactly that type, never a public method, which would look at the runtime type again. Interfaces are not
// walked for requests and streams: one handler has to be chosen, and the generator rejects handlers declared for an
// interface. An event has a route per level instead, every declared type it is, and reaches all of them.

internal abstract class RequestRoute
{
    public abstract ValueTask<object?> Dispatch(DefaultMediator mediator, object message, CancellationToken cancellationToken);
}

internal abstract class RequestRoute<TResponse> : RequestRoute
{
    public abstract ValueTask<TResponse> Dispatch(
        DefaultMediator mediator,
        IRequest<TResponse> message,
        CancellationToken cancellationToken
    );
}

internal sealed class ResponseRequestRoute<TRequest, TResponse> : RequestRoute<TResponse> where TRequest : IRequest<TResponse>
{
    public override ValueTask<TResponse> Dispatch(
        DefaultMediator mediator,
        IRequest<TResponse> message,
        CancellationToken cancellationToken
    ) => mediator.RequestAs<TRequest, TResponse>(MessageRoutes.Cast<TRequest>(message), cancellationToken);

    public override ValueTask<object?> Dispatch(DefaultMediator mediator, object message, CancellationToken cancellationToken) =>
        MediatorBox.AsObject(mediator.RequestAs<TRequest, TResponse>(MessageRoutes.Cast<TRequest>(message), cancellationToken));
}

internal abstract class VoidRequestRoute : RequestRoute
{
    public abstract ValueTask DispatchVoid(DefaultMediator mediator, object message, CancellationToken cancellationToken);
}

internal sealed class VoidRequestRoute<TRequest> : VoidRequestRoute where TRequest : IRequest
{
    public override ValueTask DispatchVoid(DefaultMediator mediator, object message, CancellationToken cancellationToken) =>
        mediator.RequestAs(MessageRoutes.Cast<TRequest>(message), cancellationToken);

    public override ValueTask<object?> Dispatch(DefaultMediator mediator, object message, CancellationToken cancellationToken) =>
        MediatorBox.AsObjectVoid(DispatchVoid(mediator, message, cancellationToken));
}

// A level of an event: one declared type the event is, whose handlers all receive it.
internal abstract class EventRoute
{
    /// <summary>Publishes to the handlers of exactly this route's type.</summary>
    public abstract ValueTask PublishLevel(
        DefaultMediator mediator,
        object message,
        IEventPublisher? publisher,
        CancellationToken cancellationToken
    );

    /// <summary>The executors of the handlers of exactly this route's type, for a publisher that receives every level.</summary>
    public abstract EventHandlerExecutor[] Executors(DefaultMediator mediator);

    /// <summary>Whether <paramref name="mediator" /> keeps the executors of this level, so that they stay the same.</summary>
    public abstract bool IsKeptBy(DefaultMediator mediator);
}

// The levels of one runtime type of event, in publish order, as one routing found them. An event of several levels also
// takes a cache slot, where a mediator that keeps every level's executors keeps them together for a publisher that
// receives every level at once.
internal sealed class EventLevelSet(EventRoute[] routes, Routing routing)
{
    public readonly int ExecutorsSlot = routes.Length > 1 ? MediatorRuntime.NextSlot() : -1;
    public readonly EventRoute[] Routes = routes;

    /// <summary>The routing that found these levels, which a later assembly may have replaced.</summary>
    public readonly Routing Routing = routing;
}

internal sealed class EventRoute<TEvent> : EventRoute where TEvent : IEvent
{
    public override ValueTask PublishLevel(
        DefaultMediator mediator,
        object message,
        IEventPublisher? publisher,
        CancellationToken cancellationToken
    ) => mediator.PublishAs(MessageRoutes.Cast<TEvent>(message), publisher, cancellationToken);

    public override EventHandlerExecutor[] Executors(DefaultMediator mediator) => mediator.ExecutorsOf<TEvent>();

    public override bool IsKeptBy(DefaultMediator mediator) => mediator.KeepsEntry<TEvent>();
}

internal abstract class StreamRoute
{
    public abstract IAsyncEnumerable<object?> Dispatch(DefaultMediator mediator, object message, CancellationToken cancellationToken);
}

internal abstract class StreamRoute<TResponse> : StreamRoute
{
    public abstract IAsyncEnumerable<TResponse> Dispatch(
        DefaultMediator mediator,
        IStreamRequest<TResponse> message,
        CancellationToken cancellationToken
    );
}

internal sealed class StreamRoute<TRequest, TResponse> : StreamRoute<TResponse> where TRequest : IStreamRequest<TResponse>
{
    public override IAsyncEnumerable<TResponse> Dispatch(
        DefaultMediator mediator,
        IStreamRequest<TResponse> message,
        CancellationToken cancellationToken
    ) => mediator.RequestStreamAs<TRequest, TResponse>(MessageRoutes.Cast<TRequest>(message), cancellationToken);

    public override IAsyncEnumerable<object?> Dispatch(DefaultMediator mediator, object message, CancellationToken cancellationToken) =>
        MediatorBox.AsObjectStream(
            mediator.RequestStreamAs<TRequest, TResponse>(MessageRoutes.Cast<TRequest>(message), cancellationToken), cancellationToken);
}

/// <summary>Open-addressing map from an exact runtime type to a route. Reads take no lock and no allocation.</summary>
internal sealed class RouteTable<TRoute> where TRoute : class
{
    private readonly Lock _gate = new();

    private Buckets _buckets = new(64);
    private int _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TRoute? Find(Type type)
    {
        var key = type.TypeHandle.Value;
        var buckets = _buckets;
        var keys = buckets.Keys;
        var index = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> buckets.Shift);
        while (true)
        {
            var candidate = Volatile.Read(ref keys[index]);
            if (candidate == key) return buckets.Values[index];
            if (candidate == 0) return null;

            index = (index + 1) & buckets.Mask;
        }
    }

    public void Add(Type type, TRoute route)
    {
        lock (_gate)
        {
            if (Find(type) is not null) return;
            if ((_count + 1) * 2 > _buckets.Keys.Length) _buckets = Grow(_buckets);

            Insert(_buckets, type.TypeHandle.Value, route);
            _count++;
        }
    }

    private static void Insert(Buckets buckets, nint key, TRoute route)
    {
        var index = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> buckets.Shift);
        while (buckets.Keys[index] != 0) index = (index + 1) & buckets.Mask;

        buckets.Values[index] = route;
        Volatile.Write(ref buckets.Keys[index], key);
    }

    private static Buckets Grow(Buckets old)
    {
        var grown = new Buckets(old.Keys.Length * 2);
        for (var i = 0; i < old.Keys.Length; i++)
            if (old.Keys[i] != 0)
                Insert(grown, old.Keys[i], old.Values[i]!);

        return grown;
    }

    private sealed class Buckets(int size)
    {
        public readonly nint[] Keys = new nint[size];
        public readonly int Mask = size - 1;
        public readonly int Shift = 64 - BitOperations.Log2((uint)size);
        public readonly TRoute?[] Values = new TRoute?[size];
    }
}

/// <summary>
/// The resolvers of every assembly initialized so far, and what dispatch has learned from them: the route of each declared type it has met, the route
/// each runtime type walked to, and the levels of each runtime type of event.
/// </summary>
/// <remarks>
/// An assembly that adds resolvers replaces the whole routing, never a part of it. A dispatch that resolves or walks takes one routing and answers from
/// it alone, so a dispatch that races an assembly's initialization answers as if it ran wholly before the assembly or wholly after it, and what it
/// learns stays in the routing it took, which no later dispatch reads. The routes of declared types are found again by the routing that replaces this
/// one: a routing that took them from another could answer with a type its own resolvers do not declare.
/// </remarks>
internal sealed class Routing
{
    /// <summary>The levels found for each runtime type of event.</summary>
    public readonly RouteTable<EventLevelSet> EventLevels = new();

    /// <inheritdoc cref="Requests" />
    public readonly RouteTable<EventRoute> Events = new();

    /// <summary>The route of each declared type this routing has met.</summary>
    public readonly RouteTable<RequestRoute> Requests = new();

    /// <inheritdoc cref="Requests" />
    public readonly RouteTable<StreamRoute> Streams = new();

    /// <summary>The route each runtime type walked to.</summary>
    public readonly RouteTable<RequestRoute> WalkedRequests = new();

    /// <inheritdoc cref="WalkedRequests" />
    public readonly RouteTable<StreamRoute> WalkedStreams = new();
    private readonly Action<object, ISet<Type>>[] _eventInterfaceMatchers;
    private readonly Func<Type, object?>[] _eventResolvers;
    private readonly Func<Type, object?>[] _requestResolvers;
    private readonly Func<Type, object?>[] _streamResolvers;

    /// <summary>The routing before any assembly has added resolvers.</summary>
    public Routing() : this([], [], [], [])
    {
    }

    private Routing(
        Func<Type, object?>[] requestResolvers,
        Func<Type, object?>[] eventResolvers,
        Action<object, ISet<Type>>[] eventInterfaceMatchers,
        Func<Type, object?>[] streamResolvers
    )
    {
        _requestResolvers = requestResolvers;
        _eventResolvers = eventResolvers;
        _eventInterfaceMatchers = eventInterfaceMatchers;
        _streamResolvers = streamResolvers;
    }

    /// <summary>A routing of these resolvers and one more assembly's, which has learned nothing yet.</summary>
    public Routing With(
        Func<Type, object?> requests,
        Func<Type, object?> events,
        Action<object, ISet<Type>>? eventInterfaces,
        Func<Type, object?> streams
    ) => new(
        [.. _requestResolvers, requests], [.. _eventResolvers, events],
        eventInterfaces is null ? _eventInterfaceMatchers : [.. _eventInterfaceMatchers, eventInterfaces],
        [.. _streamResolvers, streams]);

    /// <summary>The route declared for exactly <paramref name="type" />, created on first use.</summary>
    public RequestRoute? ResolveRequest(Type type) => Resolve(Requests, _requestResolvers, type);

    /// <inheritdoc cref="ResolveRequest" />
    private EventRoute? ResolveEvent(Type type) => Resolve(Events, _eventResolvers, type);

    /// <inheritdoc cref="ResolveRequest" />
    public StreamRoute? ResolveStream(Type type) => Resolve(Streams, _streamResolvers, type);

    /// <summary>
    /// Every declared type <paramref name="message" /> is, in publish order: its own type and its base classes from the nearest, then its interfaces by full
    /// name. Generated matchers report the interfaces with an <c>is</c> check, which keeps them through trimming where listing them by reflection would not.
    /// Remembered for the runtime type in <see cref="EventLevels" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EventLevelSet LevelsOf(object message)
    {
        var type = message.GetType();
        List<EventRoute> levels = [];
        for (var matchType = type; matchType is not null; matchType = matchType.BaseType)
            if (ResolveEvent(matchType) is { } route)
                levels.Add(route);

        var interfaces = new HashSet<Type>();
        foreach (var matcher in _eventInterfaceMatchers) matcher(message, interfaces);

        var ordered = interfaces.ToArray();
        Array.Sort(ordered, static (left, right) => string.CompareOrdinal(left.FullName, right.FullName));
        foreach (var interfaceType in ordered) levels.Add(ResolveEvent(interfaceType)!);

        var result = new EventLevelSet([.. levels], this);
        EventLevels.Add(type, result);
        return result;
    }

    private static TRoute? Resolve<TRoute>(RouteTable<TRoute> table, Func<Type, object?>[] resolvers, Type type)
        where TRoute : class
    {
        if (table.Find(type) is { } known) return known;

        foreach (var resolver in resolvers)
            if (resolver(type) is { } created)
            {
                var route = (TRoute)created;
                table.Add(type, route);
                return route;
            }

        return null;
    }
}

internal static class MessageRoutes
{
    private static readonly Lock Gate = new();
    private static readonly List<Action> CompleteEvents = [];
    private static Routing _routing = new();

    // The current routing's tables of what dispatch has walked to, kept beside it so that a dispatch that finds its
    // answer there reads one field rather than two. A dispatch that misses walks the current routing and adds to its
    // tables, so each table only ever holds the answers of the routing it belongs to.
    private static RouteTable<RequestRoute> _walkedRequests = _routing.WalkedRequests;
    private static RouteTable<StreamRoute> _walkedStreams = _routing.WalkedStreams;
    private static RouteTable<EventLevelSet> _eventLevels = _routing.EventLevels;

    /// <summary>The routing a dispatch resolves or walks in, replaced when an assembly adds resolvers.</summary>
    public static Routing Current => Volatile.Read(ref _routing);

    /// <summary>The route each runtime type walked to in the current routing.</summary>
    public static RouteTable<RequestRoute> WalkedRequests => Volatile.Read(ref _walkedRequests);

    /// <inheritdoc cref="WalkedRequests" />
    public static RouteTable<StreamRoute> WalkedStreams => Volatile.Read(ref _walkedStreams);

    /// <summary>The levels found for each runtime type of event in the current routing.</summary>
    public static RouteTable<EventLevelSet> EventLevels => Volatile.Read(ref _eventLevels);

    public static void AddResolvers(
        Func<Type, object?> requests,
        Func<Type, object?> events,
        Action<object, ISet<Type>>? eventInterfaces,
        Func<Type, object?> streams
    )
    {
        lock (Gate)
        {
            var routing = _routing.With(requests, events, eventInterfaces, streams);

            // The new assembly can declare a level of an event found to reach its own level alone.
            foreach (var clear in CompleteEvents) clear();

            CompleteEvents.Clear();
            Volatile.Write(ref _routing, routing);
            Volatile.Write(ref _walkedRequests, routing.WalkedRequests);
            Volatile.Write(ref _walkedStreams, routing.WalkedStreams);
            Volatile.Write(ref _eventLevels, routing.EventLevels);
        }
    }

    /// <summary>
    /// Records that an event of exactly <typeparamref name="TEvent" /> reaches its own level alone, as levels that <paramref name="routing" /> found
    /// say. Once an assembly has replaced that routing those levels may be out of date, and nothing is recorded. An assembly that adds resolvers later
    /// clears the record.
    /// </summary>
    public static void FoundComplete<TEvent>(Routing routing) where TEvent : IEvent
    {
        lock (Gate)
        {
            if (routing != _routing || EventSlot<TEvent>.Complete) return;

            EventSlot<TEvent>.Complete = true;
            CompleteEvents.Add(static () => EventSlot<TEvent>.Complete = false);
        }
    }

    /// <summary>
    /// Whether an event of exactly <typeparamref name="TEvent" /> reaches its own level alone, which lets a typed publish use its cached entry. A publish
    /// that races an assembly adding resolvers may take either answer, as it may take either routing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsComplete<TEvent>() where TEvent : IEvent => EventSlot<TEvent>.Complete;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // A route is only reached for the message's own type or a base of it, so the reference cast cannot fail.
    public static T Cast<T>(object message) =>
        typeof(T).IsValueType ? (T)message : Unsafe.As<object, T>(ref message);
}