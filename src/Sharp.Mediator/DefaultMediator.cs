using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Publishers;

// ReSharper disable RedundantCast — the generic entry points compare a message's type through object, which the JIT
// folds into a method table comparison; see ThrowNullMessage.

namespace Sharp.Mediator;

/// <summary>The <see cref="IMediator" /> implementation. Resolves handlers from <see cref="IServiceProvider" />.</summary>
/// <remarks>
/// A request or stream request goes to the handler of the nearest type in its runtime type's class chain that declares one, whichever method it is
/// called through: the static type only types the call. The handler of each declared type is cached as a resolved chain in a per-instance array slot,
/// indexed by a <see cref="RequestSlot{TRequest,TResponse}" /> the type takes on first use, and a statically-typed call uses that slot directly when the
/// message is exactly its static type. An instance that waits for misses before it caches keeps the chains of high slots beside the array while it
/// caches few (see <see cref="SparseChains" />). Because the cache lives on the instance, its lifetime matches the mediator's DI lifetime automatically:
/// <b>Singleton</b> caches process-wide; <b>Scoped</b> caches per scope once the scope proves busy (isolated across scopes); <b>Transient</b> bypasses
/// the cache. A chain with a service registered for a shorter lifetime than the mediator's is never cached (see <see cref="ServiceRetention" />).
/// Handlers return <see cref="ValueTask" />, so a synchronously-completed request/publish allocates nothing. An event reaches the handlers of every
/// declared type it is: its own, its base classes' and its interfaces'. A runtime type resolves its route on its first dispatch and memoizes it until an
/// assembly initialized later adds routes.
/// </remarks>
internal sealed class DefaultMediator : IMediator
{
    // Slots go to messages in the order the process first touches them, so a message an application dispatches late
    // holds a high slot however few messages a mediator caches, and an array that held the slots up to it made a busy
    // request scope of that message allocate a slot for every message dispatched before it. A mediator that waits for
    // misses before it caches, as a Scoped one does, keeps the chains of slots below this one in its array, which stays
    // this short, and those of higher slots in its SparseChains, which holds a few chains in a few entries. Once it
    // caches so many that an array of their slots takes no more memory than the table, it moves them into an array that
    // holds their slots, and from then on keeps every chain there. A dispatch finds a chain in the table after it has
    // missed the array, still without a lock or an allocation, but through shared generic code that takes four times
    // the instructions of a hit in the array. So a mediator that keeps finding its chains in the table, as a long-lived
    // scope does, moves them into an array as well once those hits number a quarter of the highest slot it holds there:
    // a request scope rarely dispatches that often, and keeps its small cache. A mediator that caches from its first
    // dispatch, as a Singleton one does, lives as long as the process, which pays for an array of every slot it caches,
    // so it keeps them all in its array from the start.
    internal const int DenseSlots = 32;

    private const string RequestTaken = "a request, which implements IRequest or IRequest<TResponse>, with a handler";
    private const string StreamTaken = "a stream request, which implements IStreamRequest<TResponse>, with a handler";

    // The kinds of middleware the provider registers (see ServiceRetention.RegisteredMiddleware). An instance that has
    // not kept a message's chain, as a request scope that dispatches once or twice has not, looks its middleware up for
    // each dispatch, which took a fifth of a dispatch when the provider registers none of the kind. It takes the byte
    // after _restricted, so the instance stays 48 bytes.
    private readonly MiddlewareKinds _middleware;

    // Set when the container registers a handler, middleware or publisher that lives shorter than this mediator,
    // which the mediator must not keep (see ServiceRetention). Beside a ushort miss threshold it keeps the instance
    // at 48 bytes.
    private readonly bool _restricted;
    private readonly IServiceProvider _serviceProvider;
    private readonly ushort _warmAfterMisses;

    // Per-instance chain cache, an array indexed by the static slot indices. `object?[]` is used because a non-generic
    // mediator can't hold a closed-generic delegate field, and an array whose runtime element type is exactly object
    // skips the covariance type-check on store. It stays empty until the instance has missed _warmAfterMisses times,
    // which a request scope dispatching one or two messages would pay for without ever hitting it. No message takes
    // slot 0, so the first element holds the mediator's SparseChains, if it has any: the chains it caches for slots
    // past the array's end.
    private object?[] _chains = [];
    private int _misses;

    // The configured publisher, resolved by the first publish that needs it rather than by each: an event of several
    // levels has no cached entry to keep it in, and neither has a publish on an instance that does not cache.
    private IEventPublisher? _publisher;

    // The constructor the container calls (see Descriptor), and the only public one: a container chooses among the
    // public constructors, and Microsoft's rejects two it can call unless one takes every parameter type of the other.
    /// <param name="serviceProvider">Resolves handlers, middleware and the event publisher.</param>
    /// <param name="retention">What the provider's registrations let this mediator keep, and from which miss.</param>
    public DefaultMediator(IServiceProvider serviceProvider, ServiceRetention retention)
        : this(serviceProvider, retention.WarmAfterMisses, retention.IsRestricted, retention.RegisteredMiddleware)
    {
    }

    /// <param name="serviceProvider">Resolves handlers, middleware and the event publisher.</param>
    /// <param name="warmAfterMisses">Misses before this instance starts caching; 0 never caches.</param>
    internal DefaultMediator(IServiceProvider serviceProvider, int warmAfterMisses = 0)
        : this(serviceProvider, warmAfterMisses, false, MiddlewareKinds.All)
    {
    }

    // restricted: whether the container registers a service that lives shorter than this mediator, in which case the
    // container's ServiceRetention says which chains, and whether the publisher, the mediator may keep.
    // middleware: the kinds of middleware the container registers; the mediator looks up no other kind.
    private DefaultMediator(
        IServiceProvider serviceProvider,
        int warmAfterMisses,
        bool restricted,
        MiddlewareKinds middleware
    )
    {
        _serviceProvider = serviceProvider;
        _warmAfterMisses = checked((ushort)warmAfterMisses);
        _restricted = restricted;
        _middleware = middleware;
    }

    // Looked up for each dispatch whose chain the mediator may not keep, through GetService, as GetRequiredService
    // first checks whether the provider implements ISupportRequiredService. A field for it would make such a dispatch
    // cheaper still, but every mediator 8 bytes larger and its creation dearer.
    private ServiceRetention Retention => (ServiceRetention)_serviceProvider.GetService(typeof(ServiceRetention))!;

    private IEventPublisher Publisher => _publisher ?? ResolvePublisher();

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask Request<TRequest>(TRequest message, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        if (message == null!) ThrowNullMessage();

        var index = VoidRequestSlot<TRequest>.Index;
        var array = _chains;
        if ((uint)index < (uint)array.Length && array[index] is { } cached && ((object)message).GetType() == typeof(TRequest))
            return Unsafe.As<MessageHandle<TRequest>>(cached)(message, cancellationToken);

        // A miss on an exact instance of a declared type resolves the handler from here. Through RequestVoidSlow and
        // RequestAs it would cost another call frame, a second cache lookup and the runtime lookups of their generic
        // statics, which a request scope pays on each dispatch until it caches.
        return VoidRequestSlot<TRequest>.Declared && (typeof(TRequest).IsValueType || ((object)message).GetType() == typeof(TRequest))
            ? RequestAsSlow(message, cancellationToken)
            : RequestVoidSlow(message, cancellationToken);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<TResponse> Request<TRequest, TResponse>(TRequest message, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
    {
        if (message == null!) ThrowNullMessage();

        var index = RequestSlot<TRequest, TResponse>.Index;
        var array = _chains;
        if ((uint)index < (uint)array.Length && array[index] is { } cached && ((object)message).GetType() == typeof(TRequest))
            return Unsafe.As<MessageHandle<TRequest, TResponse>>(cached)(message, cancellationToken);

        return RequestSlot<TRequest, TResponse>.Declared && (typeof(TRequest).IsValueType || ((object)message).GetType() == typeof(TRequest))
            ? RequestAsSlow<TRequest, TResponse>(message, cancellationToken)
            : RequestSlow<TRequest, TResponse>(message, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TResponse> Request<TResponse>(IRequest<TResponse> message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ResponseRouteOf(message) is { } route
            ? route.Dispatch(this, message, cancellationToken)
            : throw NoHandler(message.GetType(), $"IRequest<{typeof(TResponse)}>");
    }

    /// <inheritdoc />
    public ValueTask<object?> Request(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return RequestRouteOf(message) is { } route
            ? route.Dispatch(this, message, cancellationToken)
            : throw NoRequestRoute(message);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask Publish<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : IEvent
    {
        if (message == null!) ThrowNullMessage();

        // An event of exactly TEvent recorded as complete reaches its own level alone, so it goes from here to the
        // cached entry, or on a miss to the handlers PublishAsSlow resolves; any other event goes to PublishSlow,
        // which finds its levels. Through PublishSlow a miss would also cost another call frame, the lookup of its
        // levels and a second cache lookup, which a request scope pays on each publish until it caches. These lines
        // repeat PublishAs because, inlined one level deeper, it adds a ValueTask temporary that costs a cached
        // publish four instructions.
        if (((object)message).GetType() != typeof(TEvent) || !MessageRoutes.IsComplete<TEvent>())
            return PublishSlow(message, null, cancellationToken);

        var index = EventSlot<TEvent>.Index;
        var array = _chains;
        if ((uint)index >= (uint)array.Length || array[index] is not { } cached)
            return PublishAsSlow(message, null, cancellationToken);

        var entry = Unsafe.As<EventEntry<TEvent>>(cached);
        if (entry.Publisher is not SequentialPublisher)
            return entry.Publish(entry.Publisher, message, cancellationToken);

        // The default publisher's loop runs here too, rather than in SequentialPublishing, and so in PublishExact,
        // which interceptors inline. In an interceptor, which is code of one event type's own, it calls each
        // handler from a call site of that event's own, where the JIT can guess the handler behind it: in the
        // shared loop every event the application publishes calls its handlers from one call site, and the JIT can
        // guess the handler of one event at most. In EventEntry, one level deeper again, the loop costs a cached
        // publish four instructions more.
        //
        // The JIT guesses one handler at each call site, so the first handler is called apart from the loop: an event
        // of two handlers gives each call site one, where a single call site would leave the second to its delegate on
        // every publish. A single handler's task is the publish's own and is returned as it is, which the caller awaits
        // as it would the loop's, without the loop or its check of the task.
        //
        // The length is compared twice rather than switched on, which sign-extends it first: an instruction more
        // on every publish. Each handler's task has a variable of its own, which lets the analyzers see that each
        // is consumed once.
        var chains = entry.Chains;

        // ReSharper disable once ConvertIfStatementToSwitchStatement — keep the two length checks of the measured
        // publish fast path.
        if (chains.Length == 1) return chains[0](message, cancellationToken);
        if (chains.Length == 0) return default;

        var first = chains[0](message, cancellationToken);
        if (!first.IsCompletedSuccessfully)
            return SequentialPublishing.AwaitRest(first, chains, 0, message, cancellationToken);

        first.GetAwaiter().GetResult();

        for (var i = 1; i < chains.Length; i++)
        {
            var task = chains[i](message, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                return SequentialPublishing.AwaitRest(task, chains, i, message, cancellationToken);

            task.GetAwaiter().GetResult();
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask Publish<TEvent>(
        TEvent message,
        IEventPublisher? publisher,
        CancellationToken cancellationToken = default
    ) where TEvent : IEvent
    {
        if (publisher is null) return Publish(message, cancellationToken);
        if (message == null!) ThrowNullMessage();

        // As Publish.
        if (((object)message).GetType() != typeof(TEvent) || !MessageRoutes.IsComplete<TEvent>())
            return PublishSlow(message, publisher, cancellationToken);

        var index = EventSlot<TEvent>.Index;
        var array = _chains;
        return (uint)index < (uint)array.Length && array[index] is { } cached
            ? Unsafe.As<EventEntry<TEvent>>(cached).Publish(publisher, message, cancellationToken)
            : PublishAsSlow(message, publisher, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask Publish(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var levels = MessageRoutes.EventLevels.Find(message.GetType()) ?? MessageRoutes.Current.LevelsOf(message);
        return levels.Routes.Length switch
        {
            0 => message is IEvent @event
                ? PublishToNone(@event, null, cancellationToken)
                : throw CannotDispatch(message, "is not an event", "an event, which implements IEvent"),
            1 => levels.Routes[0].PublishLevel(this, message, null, cancellationToken),
            _ => PublishLevels(levels, message, null, cancellationToken)
        };
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IAsyncEnumerable<TResponse> RequestStream<TRequest, TResponse>(
        TRequest message,
        CancellationToken cancellationToken = default
    ) where TRequest : IStreamRequest<TResponse>
    {
        if (message == null!) ThrowNullMessage();

        var index = StreamRequestSlot<TRequest, TResponse>.Index;
        var array = _chains;
        if ((uint)index < (uint)array.Length && array[index] is { } cached && ((object)message).GetType() == typeof(TRequest))
        {
            var chain = Unsafe.As<StreamMessageHandle<TRequest, TResponse>>(cached);
            return StreamCancellation.Pass(chain, chain(message, cancellationToken), cancellationToken);
        }

        // As in Request<TRequest>.
        return StreamRequestSlot<TRequest, TResponse>.Declared && (typeof(TRequest).IsValueType || ((object)message).GetType() == typeof(TRequest))
            ? RequestStreamAsSlow<TRequest, TResponse>(message, cancellationToken)
            : RequestStreamSlow<TRequest, TResponse>(message, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> RequestStream<TResponse>(
        IStreamRequest<TResponse> message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);
        return ItemStreamRouteOf(message) is { } route
            ? route.Dispatch(this, message, cancellationToken)
            : throw NoHandler(message.GetType(), $"IStreamRequest<{typeof(TResponse)}>");
    }

    /// <inheritdoc />
    public IAsyncEnumerable<object?> RequestStream(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return StreamRouteOf(message) is { } route
            ? route.Dispatch(this, message, cancellationToken)
            : throw NoStreamRoute(message);
    }

    // Where the process compiles dynamic code, the mediator is registered by its type: Microsoft's container then
    // compiles how it creates one and builds the provider's ServiceRetention singleton into that code, where a
    // factory looked the singleton up for each mediator. Without dynamic code, as under Native AOT, that container
    // calls a constructor through reflection with an array of its arguments, which costs more than the lookup, so a
    // factory looks it up there. It does so through GetService, as GetRequiredService first checks whether the
    // provider implements ISupportRequiredService.
    internal static ServiceDescriptor Descriptor(ServiceLifetime lifetime, bool dynamicCodeCompiled) =>
        dynamicCodeCompiled
            ? new ServiceDescriptor(typeof(IMediator), typeof(DefaultMediator), lifetime)
            : new ServiceDescriptor(typeof(IMediator), static services => new DefaultMediator(
                services, (ServiceRetention)services.GetService(typeof(ServiceRetention))!), lifetime);

    // Asked before a chain is built: an instance that will not keep it reaches a handler with no middleware directly,
    // since creating a delegate from an interface method is among the most expensive steps of a per-scope dispatch.
    private bool ShouldCache() =>
        _warmAfterMisses != 0 &&
        (Volatile.Read(ref _chains).Length != 0 || Interlocked.Increment(ref _misses) >= _warmAfterMisses);

    // Whether the array of this mediator holds every slot it caches, from the start or since its chains moved into it.
    private bool HoldsEverySlot(object?[] chains) => _warmAfterMisses == 1 || chains.Length > DenseSlots;

    private void Cache(int index, object chain) => Cache(Volatile.Read(ref _chains), index, chain);

    // Writes chain into the cache of the array this dispatch read, `chains`, or of the one that has replaced it. A
    // write past the array's end, which grows it or changes the SparseChains it holds, takes the lock of the array and
    // checks under it that the array is still this mediator's, so a replacement copies everything written before it,
    // and a write that finds its array replaced moves on to the replacement. A request scope writes most of its chains
    // inside its array, though, one for each message it caches after its first, and such a write takes no lock: it
    // writes the slot as a full fence, then reads the mediator's array again and writes into the replacement it finds
    // there, while a replacement published after that read carries the write over (see Replace). No chain is lost to
    // another dispatch growing the cache, however often it grows. Dispatch reads the cache without the lock.
    internal void Cache(object?[] chains, int index, object chain)
    {
        Debug.Assert(index > 0, "Slot 0 holds the mediator's SparseChains.");

        while (true)
        {
            // Every mediator starts with the same empty array. It holds nothing to lose and is never locked: the first
            // array replaces it with a compare-and-swap, and a dispatch that loses that race writes into the winner's.
            if (chains.Length == 0)
            {
                object?[] first;
                if (index < DenseSlots || HoldsEverySlot(chains))
                {
                    first = new object?[index + 1];
                    first[index] = chain;
                }
                else first = [new SparseChains(index, chain)];

                var installed = Interlocked.CompareExchange(ref _chains, first, chains);
                if (installed == chains) return;

                chains = installed;

                continue;
            }

            if ((uint)index < (uint)chains.Length)
            {
                Interlocked.Exchange(ref chains[index], chain);
                var current = Volatile.Read(ref _chains);
                if (current == chains) return;

                chains = current;

                continue;
            }

            lock (chains)
            {
                if (Volatile.Read(ref _chains) == chains)
                {
                    Store(chains, index, chain);
                    return;
                }
            }

            chains = Volatile.Read(ref _chains);
        }
    }

    // Under the lock of chains, which is this mediator's array, for a slot past its end.
    private void Store(object?[] chains, int index, object chain)
    {
        Debug.Assert(index >= chains.Length, "A slot inside the array is written without the lock.");

        // Growing at least twofold keeps a long-lived mediator, which meets new messages one at a time, from copying
        // its array once for each.
        var holdsEverySlot = HoldsEverySlot(chains);
        if (holdsEverySlot || index < DenseSlots)
        {
            var length = Math.Max(chains.Length * 2, index + 1);
            var grown = new object?[holdsEverySlot ? length : Math.Min(length, DenseSlots)];
            Array.Copy(chains, grown, chains.Length);
            grown[index] = chain;
            Replace(chains, grown);

            return;
        }

        if ((SparseChains?)chains[0] is not { } sparse)
            Volatile.Write(ref chains[0], new SparseChains(index, chain));
        else if (!sparse.TryAdd(index, chain))
        {
            var moved = ArrayOf(chains, sparse, Math.Max(sparse.HighestSlot, index));
            moved[index] = chain;
            Replace(chains, moved);
        }
    }

    // Under the lock of chains: makes replacement, a copy of chains with more slots, this mediator's array. A
    // dispatch writes a slot inside an array without the lock, as a full fence before it reads the mediator's array
    // again (see Cache), so it can write into chains after the copy was made and still read chains as the mediator's.
    // Publishing replacement is a full fence before this reads chains again, so it then finds every such write, and
    // writes the chain into replacement as a dispatch would, unless the slot there holds one already: two chains of
    // one message wrap the same instances, since a mediator keeps only a chain whose services live as long as it
    // does, so either serves.
    internal void Replace(object?[] chains, object?[] replacement)
    {
        Interlocked.Exchange(ref _chains, replacement);
        for (var i = 1; i < chains.Length; i++)
            if (Volatile.Read(ref chains[i]) is { } chain && Volatile.Read(ref replacement[i]) is null)
                Cache(replacement, i, chain);
    }

    // Under the lock of chains: an array of every slot it and its SparseChains hold, up to highest, to replace it.
    private static object?[] ArrayOf(object?[] chains, SparseChains sparse, int highest)
    {
        var moved = new object?[Math.Max(chains.Length * 2, highest + 1)];
        Array.Copy(chains, 1, moved, 1, chains.Length - 1);
        sparse.CopyTo(moved);
        return moved;
    }

    // Moves the chains of the SparseChains that the array the dispatch read holds, `chains`, or that the array that has
    // replaced it holds, into an array of every slot, under the lock of the array it replaces, as a write past an
    // array's end does (see Cache). A replacement without a table has had its chains moved already.
    internal void MoveToArray(object?[] chains)
    {
        while (true)
        {
            lock (chains)
            {
                if (Volatile.Read(ref _chains) == chains)
                {
                    if ((SparseChains?)chains[0] is { } sparse)
                        Replace(chains, ArrayOf(chains, sparse, sparse.HighestSlot));

                    return;
                }
            }

            chains = Volatile.Read(ref _chains);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // The chain this mediator caches for a slot, wherever it keeps it, or null: what a dispatch looks for when its
    // array misses, before it resolves anything. It reads the array again, which may have replaced the one the dispatch
    // read. A slot inside the array is read here, since a request scope misses one for each message it caches after its
    // first, and only a slot past the array's end calls out.
    internal object? Lookup(int index)
    {
        var chains = _chains;
        return chains.Length == 0 ? null : Lookup(chains, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Lookup in chains, an array the dispatch has just read and found holding something. A miss reads the array
    // before the slot, and looks only when the array holds something: the slot of a message that is a class is a
    // static of shared generic code, read through a call to a runtime helper, and a request scope misses several
    // times before it caches anything.
    private object? Lookup(object?[] chains, int index)
    {
        Debug.Assert(index > 0, "Slot 0 holds the mediator's SparseChains.");
        Debug.Assert(chains.Length != 0, "The empty array holds nothing.");
        return (uint)index < (uint)chains.Length ? chains[index] : LookupBeside(chains, index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    // The chain the SparseChains of chains holds for a slot past the array's end, or null.
    private object? LookupBeside(object?[] chains, int index)
    {
        if ((SparseChains?)chains[0] is not { } sparse || sparse.Find(index) is not { } chain) return null;
        if (sparse.CountHit()) MoveToArray(chains);

        return chain;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // The chain this instance's array holds for a message type, or null, in which case the call takes the uncached
    // route, which also finds a chain kept beside the array. MediatorCalls invokes it from an instance of that
    // message's own, where the JIT profiles the call per message. Looking beside the array from here would put a call
    // that returns into every call site, and the JIT would keep the call's arguments in callee-saved registers across
    // it, which every hit would then save and restore.
    internal MessageHandle<TRequest>? CachedChain<TRequest>() where TRequest : IRequest =>
        Cached<MessageHandle<TRequest>>(VoidRequestSlot<TRequest>.Index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageHandle<TRequest, TResponse>? CachedChain<TRequest, TResponse>() where TRequest : IRequest<TResponse> =>
        Cached<MessageHandle<TRequest, TResponse>>(RequestSlot<TRequest, TResponse>.Index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StreamMessageHandle<TRequest, TResponse>? CachedStreamChain<TRequest, TResponse>()
        where TRequest : IStreamRequest<TResponse> =>
        Cached<StreamMessageHandle<TRequest, TResponse>>(StreamRequestSlot<TRequest, TResponse>.Index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T? Cached<T>(int index) where T : class
    {
        var array = _chains;
        return (uint)index < (uint)array.Length ? Unsafe.As<T>(array[index]) : null;
    }

    // Kept for later publishes unless it is registered with a shorter lifetime than the mediator's.
    private IEventPublisher ResolvePublisher()
    {
        var publisher = _serviceProvider.GetRequiredService<IEventPublisher>();
        if (!_restricted || Retention.KeepsPublisher) _publisher = publisher;

        return publisher;
    }

    // DI hands back an array, but IEnumerable is all a container promises.
    private static T[] ToArray<T>(IEnumerable<T> services) => services as T[] ?? [.. services];

    [DoesNotReturn]
    // The generic entry points compare a message's type through object, `((object) message).GetType()`: called on the
    // type parameter, GetType() is a constrained call that the JIT does not fold into a method table comparison, which
    // left shared generic code a call to a runtime helper and inlined code a RuntimeType to load.
    //
    // The generic entry points test `message == null` rather than call ArgumentNullException.ThrowIfNull: the test
    // folds away for a value-type message, while passing one to an object parameter boxes it until the JIT
    // optimizes the call.

    // ReSharper disable once NotResolvedInText — the name is the parameter of the generic entry points that
    // call this helper.
    private static void ThrowNullMessage() => throw new ArgumentNullException("message");

    // A message without a handler is a configuration the mediator cannot serve, whichever entry point it reaches, so
    // every one throws the same type for it. Only an object that is not the kind of message an object entry point takes
    // is an argument error.
    private static InvalidOperationException NoHandler(Type messageType, string? dispatchedAs = null) => new(
        $"No handler is registered for message type '{messageType}'{(dispatchedAs is null ? "" : $" as {dispatchedAs}")}. " +
        "An assembly compiled with the Sharp.Mediator source generator must declare a handler for it or for one of its " +
        "base classes, and AddMediator must register that handler.");

    // An object an object entry point cannot dispatch at all. The message says what the object was found to be, which
    // holds even for a type that is two kinds of message at once, and what the method takes.
    private static ArgumentException CannotDispatch(object message, string fact, string takes) => new(
        $"'{message.GetType()}' {fact}, so this method cannot dispatch it: it takes {takes}.", nameof(message));

    // A request with no route has no handler, unless it is another kind of object: not a message, an event, or a stream
    // request, which only the stream routes tell apart, since the two differ by an interface that takes a type
    // argument. Run on failure alone, so the lookups cost a dispatch nothing.
    private static Exception NoRequestRoute(object message) =>
        message is IRequest
            ? NoHandler(message.GetType(), "IRequest or IRequest<TResponse>")
            : message is not IMessage
                ? CannotDispatch(message, "is not a message", RequestTaken)
                : message is IEvent
                    ? CannotDispatch(message, "is an event with no request handler", RequestTaken)
                    : StreamRouteOf(message) is not null
                        ? CannotDispatch(message, "has a stream handler and no request handler", RequestTaken)
                        : NoHandler(message.GetType(), "IRequest or IRequest<TResponse>");

    // As NoRequestRoute, for a stream request: a request is told apart by its interface when it returns nothing, and by
    // the request routes otherwise.
    private static Exception NoStreamRoute(object message) =>
        message is not IMessage
            ? CannotDispatch(message, "is not a message", StreamTaken)
            : message is IEvent
                ? CannotDispatch(message, "is an event with no stream handler", StreamTaken)
                : message is IRequest
                    ? CannotDispatch(message, "is a request with no stream handler", StreamTaken)
                    : RequestRouteOf(message) is not null
                        ? CannotDispatch(message, "has a request handler and no stream handler", StreamTaken)
                        : NoHandler(message.GetType(), "IStreamRequest<TResponse>");

    // An event that no assembly declares a handler for has no handlers, as one whose handlers are not registered has:
    // the default publisher has none to run, and any other receives the empty list, as it receives every publish.
    // Generic, so a value-type event is boxed only for another publisher.
    private ValueTask PublishToNone<TEvent>(TEvent message, IEventPublisher? publisher, CancellationToken cancellationToken) where TEvent : IEvent
    {
        publisher ??= Publisher;
        return publisher is SequentialPublisher ? default : publisher.Publish([], message, cancellationToken);
    }

    // The route for what a message is: its own type's, or the nearest base class's that declares a handler. The walk
    // happens once per runtime type, which then remembers the route until an assembly adds resolvers. A walk takes one
    // routing and adds its answer there, so one that an assembly's initialization interrupts neither mixes the routing
    // before the assembly with the one after it nor leaves an answer of the old routing in the new one.
    private static RequestRoute? RequestRouteOf(object message) =>
        MessageRoutes.WalkedRequests.Find(message.GetType()) ?? WalkToRequestRoute(message.GetType());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RequestRoute? WalkToRequestRoute(Type type)
    {
        var routing = MessageRoutes.Current;
        for (var matchType = type; matchType is not null; matchType = matchType.BaseType)
            if (routing.ResolveRequest(matchType) is { } route)
            {
                routing.WalkedRequests.Add(type, route);
                return route;
            }

        return null;
    }

    // A base declared with another response never matches, so its handler never runs for this call.
    private static RequestRoute<TResponse>? ResponseRouteOf<TResponse>(IRequest<TResponse> message) =>
        MessageRoutes.WalkedRequests.Find(message.GetType()) as RequestRoute<TResponse> ??
        WalkToResponseRoute<TResponse>(message.GetType());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RequestRoute<TResponse>? WalkToResponseRoute<TResponse>(Type type)
    {
        var routing = MessageRoutes.Current;
        for (var matchType = type; matchType is not null; matchType = matchType.BaseType)
            if (routing.ResolveRequest(matchType) is RequestRoute<TResponse> route)
            {
                routing.WalkedRequests.Add(type, route);
                return route;
            }

        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    // A value type is always exactly its static type, so only its own route can apply, and finding it needs no box.
    // Anything else goes to the route of what it is. Either way a handler only runs where an assembly declares one. The
    // first dispatch that finds TRequest's own route marks TRequest declared, and Request then sends an exact instance
    // straight to its handler: nothing is nearer to it, and no assembly replaces a declared type's route.
    private ValueTask RequestVoidSlow<TRequest>(TRequest message, CancellationToken cancellationToken) where TRequest : IRequest
    {
        if (typeof(TRequest).IsValueType)
        {
            if (MessageRoutes.Current.ResolveRequest(typeof(TRequest)) is not VoidRequestRoute)
                throw NoHandler(typeof(TRequest));

            VoidRequestSlot<TRequest>.Declared = true;

            return RequestAs(message, cancellationToken);
        }

        if (RequestRouteOf(message) is not VoidRequestRoute route) throw NoHandler(message.GetType());
        if (route is VoidRequestRoute<TRequest>) VoidRequestSlot<TRequest>.Declared = true;

        return route.DispatchVoid(this, message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // The handler of exactly TRequest: the type a route names, or a value type.
    internal ValueTask RequestAs<TRequest>(TRequest message, CancellationToken cancellationToken) where TRequest : IRequest
    {
        var index = VoidRequestSlot<TRequest>.Index;
        var array = _chains;
        if ((uint)index < (uint)array.Length && array[index] is { } cached)
            return Unsafe.As<MessageHandle<TRequest>>(cached)(message, cancellationToken);

        return RequestAsSlow(message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask RequestAsSlow<TRequest>(TRequest message, CancellationToken cancellationToken) where TRequest : IRequest
    {
        var array = _chains;
        if (array.Length != 0 && Lookup(array, VoidRequestSlot<TRequest>.Index) is { } cached)
            return Unsafe.As<MessageHandle<TRequest>>(cached)(message, cancellationToken);

        var handler = _serviceProvider.GetService<IRequestHandler<TRequest>>() ?? throw NoHandler(typeof(TRequest));
        var middlewares = (_middleware & MiddlewareKinds.Message) != 0
            ? ToArray(_serviceProvider.GetServices<IMiddleware<TRequest>>())
            : [];

        if (!ShouldCache() || (_restricted && !Retention.Keeps(typeof(IRequestHandler<TRequest>), typeof(IMiddleware<TRequest>))))
        {
            return middlewares.Length == 0
                ? handler.Handle(message, cancellationToken)
                : MessageHandleBuilder.Build(handler.Handle, middlewares)(message, cancellationToken);
        }

        var chain = MessageHandleBuilder.Build(handler.Handle, middlewares);
        Cache(VoidRequestSlot<TRequest>.Index, chain);
        return chain(message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    // As RequestVoidSlow.
    private ValueTask<TResponse> RequestSlow<TRequest, TResponse>(TRequest message, CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        if (typeof(TRequest).IsValueType)
        {
            if (MessageRoutes.Current.ResolveRequest(typeof(TRequest)) is not RequestRoute<TResponse>)
                throw NoHandler(typeof(TRequest));

            RequestSlot<TRequest, TResponse>.Declared = true;

            return RequestAs<TRequest, TResponse>(message, cancellationToken);
        }

        var route = ResponseRouteOf(message) ?? throw NoHandler(message.GetType());
        if (route is ResponseRequestRoute<TRequest, TResponse>) RequestSlot<TRequest, TResponse>.Declared = true;

        return route.Dispatch(this, message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ValueTask<TResponse> RequestAs<TRequest, TResponse>(TRequest message, CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var index = RequestSlot<TRequest, TResponse>.Index;
        var array = _chains;
        if ((uint)index < (uint)array.Length && array[index] is { } cached)
            return Unsafe.As<MessageHandle<TRequest, TResponse>>(cached)(message, cancellationToken);

        return RequestAsSlow<TRequest, TResponse>(message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<TResponse> RequestAsSlow<TRequest, TResponse>(TRequest message, CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var array = _chains;
        if (array.Length != 0 && Lookup(array, RequestSlot<TRequest, TResponse>.Index) is { } cached)
            return Unsafe.As<MessageHandle<TRequest, TResponse>>(cached)(message, cancellationToken);

        var handler = _serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>() ?? throw NoHandler(typeof(TRequest));
        var middlewares = (_middleware & MiddlewareKinds.Request) != 0
            ? ToArray(_serviceProvider.GetServices<IMiddleware<TRequest, TResponse>>())
            : [];

        if (!ShouldCache() ||
            (_restricted && !Retention.Keeps(typeof(IRequestHandler<TRequest, TResponse>), typeof(IMiddleware<TRequest, TResponse>))))
        {
            return middlewares.Length == 0
                ? handler.Handle(message, cancellationToken)
                : MessageHandleBuilder.Build(handler.Handle, middlewares)(message, cancellationToken);
        }

        var chain = MessageHandleBuilder.Build(handler.Handle, middlewares);
        Cache(RequestSlot<TRequest, TResponse>.Index, chain);
        return chain(message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Publish for an instance of exactly TEvent, which an interceptor knows when nothing can derive from TEvent (a
    // value type, or a class it has found sealed when it was compiled or when the program runs) and checks otherwise.
    // Its type is not compared here: the JIT does not fold that comparison away even for a sealed class. MediatorCalls
    // sends a null message to Publish instead, so it is not tested for here either. Otherwise as Publish.
    //
    // TKey is a struct that only the interceptors of this event pass (see MediatorCalls), which makes this method an
    // instance of that event's own. The JIT guesses the handlers by the profile it collects while the method runs on
    // its own, before tier 1 and before any inlining. With TEvent alone every event of a reference type would share one
    // instance, and with it the profile of the event that warmed up first, as a publish through the interface still
    // does: from generic code, or with interceptors off.

    // ReSharper disable once UnusedTypeParameter — TKey only makes PublishExact an instance of
    // one event's interceptors.
    internal ValueTask PublishExact<TEvent, TKey>(TEvent message, CancellationToken cancellationToken) where TEvent : IEvent where TKey : struct
    {
        if (!MessageRoutes.IsComplete<TEvent>())
            return PublishSlow(message, null, cancellationToken);

        var index = EventSlot<TEvent>.Index;
        var array = _chains;
        if ((uint)index >= (uint)array.Length || array[index] is not { } cached)
            return PublishAsSlow(message, null, cancellationToken);

        var entry = Unsafe.As<EventEntry<TEvent>>(cached);
        if (entry.Publisher is not SequentialPublisher)
            return entry.Publish(entry.Publisher, message, cancellationToken);

        var chains = entry.Chains;

        // ReSharper disable once ConvertIfStatementToSwitchStatement — keep the two length checks of the measured
        // publish fast path.
        if (chains.Length == 1) return chains[0](message, cancellationToken);
        if (chains.Length == 0) return default;

        var first = chains[0](message, cancellationToken);
        if (!first.IsCompletedSuccessfully)
            return SequentialPublishing.AwaitRest(first, chains, 0, message, cancellationToken);

        first.GetAwaiter().GetResult();

        for (var i = 1; i < chains.Length; i++)
        {
            var task = chains[i](message, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                return SequentialPublishing.AwaitRest(task, chains, i, message, cancellationToken);

            task.GetAwaiter().GetResult();
        }

        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    // An event reaches every type it is that declares handlers: its own, its base classes' and its interfaces'. The
    // common case is one level, its static type's, which keeps the typed path and its cache: an event of exactly TEvent
    // with that level alone is recorded as complete, and until an assembly adds resolvers a typed publish of one goes
    // from its entry point to the cached entry or straight to the handlers, without coming here. A value type is boxed
    // only to find its levels the first time, and to publish to an interface level.
    private ValueTask PublishSlow<TEvent>(TEvent message, IEventPublisher? publisher, CancellationToken cancellationToken)
        where TEvent : IEvent
    {
        // A value type is exactly TEvent, and reading its type would box it on each publish of an event that
        // has no level.
        var type = typeof(TEvent).IsValueType ? typeof(TEvent) : message.GetType();
        var levels = MessageRoutes.EventLevels.Find(type) ?? MessageRoutes.Current.LevelsOf(message);
        var routes = levels.Routes;
        switch (routes.Length)
        {
            case 0: return PublishToNone(message, publisher, cancellationToken);
            case > 1: return PublishLevels(levels, message, publisher, cancellationToken);
        }

        if (routes[0] is not EventRoute<TEvent>)
            return routes[0].PublishLevel(this, message, publisher, cancellationToken);

        // Recorded under a lock. The typed entry points send an event of exactly TEvent here only while it is
        // not recorded, so a mediator that does not cache takes the lock on the publish that records it, not
        // on every publish.
        if (type == typeof(TEvent))
            MessageRoutes.FoundComplete<TEvent>(levels.Routing);

        return PublishAs(message, publisher, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Publishes to the handlers of exactly TEvent, through publisher or the configured one.
    internal ValueTask PublishAs<TEvent>(TEvent message, IEventPublisher? publisher, CancellationToken cancellationToken) where TEvent : IEvent
    {
        var index = EventSlot<TEvent>.Index;
        var array = _chains;
        if ((uint)index >= (uint)array.Length || array[index] is not { } cached)
            return PublishAsSlow(message, publisher, cancellationToken);

        var entry = Unsafe.As<EventEntry<TEvent>>(cached);
        return entry.Publish(publisher ?? entry.Publisher, message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask PublishAsSlow<TEvent>(TEvent message, IEventPublisher? publisher, CancellationToken cancellationToken)
        where TEvent : IEvent
    {
        var array = _chains;
        if (array.Length != 0 && Lookup(array, EventSlot<TEvent>.Index) is { } cached)
        {
            var kept = Unsafe.As<EventEntry<TEvent>>(cached);
            return kept.Publish(publisher ?? kept.Publisher, message, cancellationToken);
        }

        var handlers = ToArray(_serviceProvider.GetServices<IEventHandler<TEvent>>());
        var middlewares = (_middleware & MiddlewareKinds.Message) != 0
            ? ToArray(_serviceProvider.GetServices<IMiddleware<TEvent>>())
            : [];

        // A cached entry carries the configured publisher, which a publish that names its own does not resolve. Such a
        // publish caches nothing until a publish without a publisher has resolved it.
        var configured = publisher is null ? Publisher : _publisher;
        if (!ShouldCache() || configured is null ||
            (_restricted && !(Retention.KeepsPublisher && Retention.Keeps(typeof(IEventHandler<TEvent>), typeof(IMiddleware<TEvent>)))))
        {
            publisher ??= configured!;
            return middlewares.Length == 0 && publisher is SequentialPublisher
                ? SequentialPublishing.Publish(handlers, message, cancellationToken)
                : EventEntry<TEvent>.Publish(
                    publisher, handlers, MessageHandleBuilder.BuildEach(handlers, middlewares), message, cancellationToken);
        }

        var entry = new EventEntry<TEvent>(handlers, MessageHandleBuilder.BuildEach(handlers, middlewares), configured);
        Cache(EventSlot<TEvent>.Index, entry);
        return entry.Publish(publisher ?? configured, message, cancellationToken);
    }

    // Whether this mediator has kept the entry of exactly TEvent.
    internal bool KeepsEntry<TEvent>() where TEvent : IEvent => Lookup(EventSlot<TEvent>.Index) is not null;

    // The executors of exactly TEvent's handlers, for a publisher that receives every level of a publish: the cached
    // entry's, or new ones on an instance that does not cache yet.
    internal EventHandlerExecutor[] ExecutorsOf<TEvent>() where TEvent : IEvent
    {
        var array = _chains;
        if (array.Length != 0 && Lookup(array, EventSlot<TEvent>.Index) is { } cached)
            return Unsafe.As<EventEntry<TEvent>>(cached).Executors;

        var handlers = ToArray(_serviceProvider.GetServices<IEventHandler<TEvent>>());
        var middlewares = (_middleware & MiddlewareKinds.Message) != 0
            ? ToArray(_serviceProvider.GetServices<IMiddleware<TEvent>>())
            : [];

        var chains = MessageHandleBuilder.BuildEach(handlers, middlewares);

        // As in PublishAsSlow: the configured publisher is resolved here only when the publish uses it.
        if (!ShouldCache() || _publisher is not { } configured ||
            (_restricted && !Retention.Keeps(typeof(IEventHandler<TEvent>), typeof(IMiddleware<TEvent>))))
            return EventHandlerExecutor<TEvent>.Of(handlers, chains);

        var entry = new EventEntry<TEvent>(handlers, chains, configured);
        Cache(EventSlot<TEvent>.Index, entry);
        return entry.Executors;
    }

    // The default publisher runs a level at a time through its typed loop, which allocates nothing and is what it
    // does with every level's executors: finish one handler before the next, and stop at the first failure. Any
    // other publisher, the parallel one included, receives every level's executors in one call and decides how
    // they run together.
    private ValueTask PublishLevels(
        EventLevelSet levels,
        object message,
        IEventPublisher? publisher,
        CancellationToken cancellationToken
    )
    {
        publisher ??= Publisher;
        return publisher is SequentialPublisher
            ? PublishLevelsInOrder(levels.Routes, message, publisher, cancellationToken)
            : publisher.Publish(ExecutorsOf(levels), (IEvent)message, cancellationToken);
    }

    // Every level's executors in level order. Once this mediator keeps the executors of each level, it keeps them
    // together in the levels' own slot, so a warm publish hands the publisher the same array instead of collecting
    // a new one.
    private EventHandlerExecutor[] ExecutorsOf(EventLevelSet levels)
    {
        var slot = levels.ExecutorsSlot;
        var array = _chains;
        if ((uint)slot < (uint)array.Length && array[slot] is { } cached)
            return Unsafe.As<EventHandlerExecutor[]>(cached);

        if (Lookup(slot) is { } found)
            return Unsafe.As<EventHandlerExecutor[]>(found);

        var routes = levels.Routes;
        var parts = new EventHandlerExecutor[routes.Length][];
        var count = 0;
        for (var i = 0; i < routes.Length; i++) count += (parts[i] = routes[i].Executors(this)).Length;

        var executors = new EventHandlerExecutor[count];
        var kept = true;
        for (int i = 0, offset = 0; i < routes.Length; offset += parts[i].Length, i++)
        {
            parts[i].CopyTo(executors, offset);
            kept &= routes[i].IsKeptBy(this);
        }

        if (kept) Cache(slot, executors);

        return executors;
    }

    private ValueTask PublishLevelsInOrder(
        EventRoute[] levels,
        object message,
        IEventPublisher publisher,
        CancellationToken cancellationToken
    )
    {
        for (var i = 0; i < levels.Length; i++)
        {
            var task = levels[i].PublishLevel(this, message, publisher, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                return AwaitLevelsInOrder(task, levels, i, message, publisher, cancellationToken);

            task.GetAwaiter().GetResult();
        }

        return default;
    }

    private async ValueTask AwaitLevelsInOrder(
        ValueTask pending,
        EventRoute[] levels,
        int index,
        object message,
        IEventPublisher publisher,
        CancellationToken cancellationToken
    )
    {
        await pending.ConfigureAwait(false);

        for (var i = index + 1; i < levels.Length; i++)
            await levels[i].PublishLevel(this, message, publisher, cancellationToken).ConfigureAwait(false);
    }

    // The route for what a stream request is, as for requests.
    private static StreamRoute? StreamRouteOf(object message) =>
        MessageRoutes.WalkedStreams.Find(message.GetType()) ?? WalkToStreamRoute(message.GetType());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StreamRoute? WalkToStreamRoute(Type type)
    {
        var routing = MessageRoutes.Current;
        for (var matchType = type; matchType is not null; matchType = matchType.BaseType)
            if (routing.ResolveStream(matchType) is { } route)
            {
                routing.WalkedStreams.Add(type, route);
                return route;
            }

        return null;
    }

    private static StreamRoute<TResponse>? ItemStreamRouteOf<TResponse>(IStreamRequest<TResponse> message) =>
        MessageRoutes.WalkedStreams.Find(message.GetType()) as StreamRoute<TResponse> ??
        WalkToItemStreamRoute<TResponse>(message.GetType());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StreamRoute<TResponse>? WalkToItemStreamRoute<TResponse>(Type type)
    {
        var routing = MessageRoutes.Current;
        for (var matchType = type; matchType is not null; matchType = matchType.BaseType)
            if (routing.ResolveStream(matchType) is StreamRoute<TResponse> route)
            {
                routing.WalkedStreams.Add(type, route);
                return route;
            }

        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    // As RequestVoidSlow.
    private IAsyncEnumerable<TResponse> RequestStreamSlow<TRequest, TResponse>(
        TRequest message,
        CancellationToken cancellationToken
    ) where TRequest : IStreamRequest<TResponse>
    {
        if (typeof(TRequest).IsValueType)
        {
            if (MessageRoutes.Current.ResolveStream(typeof(TRequest)) is not StreamRoute<TResponse>)
                throw NoHandler(typeof(TRequest));

            StreamRequestSlot<TRequest, TResponse>.Declared = true;

            return RequestStreamAs<TRequest, TResponse>(message, cancellationToken);
        }

        var route = ItemStreamRouteOf(message) ?? throw NoHandler(message.GetType());
        if (route is StreamRoute<TRequest, TResponse>) StreamRequestSlot<TRequest, TResponse>.Declared = true;

        return route.Dispatch(this, message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal IAsyncEnumerable<TResponse> RequestStreamAs<TRequest, TResponse>(
        TRequest message,
        CancellationToken cancellationToken
    ) where TRequest : IStreamRequest<TResponse>
    {
        var index = StreamRequestSlot<TRequest, TResponse>.Index;
        var array = _chains;
        if ((uint)index < (uint)array.Length && array[index] is { } cached)
        {
            var chain = Unsafe.As<StreamMessageHandle<TRequest, TResponse>>(cached);
            return StreamCancellation.Pass(chain, chain(message, cancellationToken), cancellationToken);
        }

        return RequestStreamAsSlow<TRequest, TResponse>(message, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IAsyncEnumerable<TResponse> RequestStreamAsSlow<TRequest, TResponse>(
        TRequest message,
        CancellationToken cancellationToken
    ) where TRequest : IStreamRequest<TResponse>
    {
        var array = _chains;
        if (array.Length != 0 && Lookup(array, StreamRequestSlot<TRequest, TResponse>.Index) is { } cached)
        {
            var found = Unsafe.As<StreamMessageHandle<TRequest, TResponse>>(cached);
            return StreamCancellation.Pass(found, found(message, cancellationToken), cancellationToken);
        }

        var handler = _serviceProvider.GetService<IStreamRequestHandler<TRequest, TResponse>>() ?? throw NoHandler(typeof(TRequest));
        var middlewares = (_middleware & MiddlewareKinds.Stream) != 0
            ? ToArray(_serviceProvider.GetServices<IStreamMiddleware<TRequest, TResponse>>())
            : [];

        if (!ShouldCache() ||
            (_restricted &&
                !Retention.Keeps(typeof(IStreamRequestHandler<TRequest, TResponse>), typeof(IStreamMiddleware<TRequest, TResponse>))))
        {
            return middlewares.Length == 0
                ? StreamCancellation.Pass(handler.Handle(message, cancellationToken), cancellationToken)
                : StreamCancellation.Chain(handler, middlewares)(message, cancellationToken);
        }

        var chain = StreamCancellation.Chain(handler, middlewares);
        Cache(StreamRequestSlot<TRequest, TResponse>.Index, chain);
        return StreamCancellation.Pass(chain, chain(message, cancellationToken), cancellationToken);
    }
}