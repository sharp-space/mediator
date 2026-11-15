using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Fixtures;

namespace Sharp.Mediator.Tests.Dispatch;

// A module initializer can add an assembly's resolvers while another thread dispatches. Each test here holds a dispatch
// at a gate inside a resolver or an interface matcher, where it has read the routes but not yet answered, adds an
// assembly's resolvers from the test's thread, and only then opens the gate, so it pins one interleaving without timing
// anything. The dispatch held at the gate answers as the routes stood when it read them and remembers its answer only
// in the routes it read, which the assembly has replaced, so the dispatch after it sees the assembly. The routes are
// process-wide: every test owns its message types, and the resolvers it adds stay for the rest of the run, by then
// declaring only its types and holding nothing.
public sealed class MessageRoutesRaceTests
{
    private static IMediator Mediator(Action<MediatorBuilder> configure, Action<IServiceCollection> services)
    {
        var collection = new ServiceCollection();
        services(collection);
        collection.AddMediator(configure);
        return collection.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    // ---- requests and streams ---------------------------------------------------------------------------------------

    [Fact]
    // An assembly declares the request a walk is looking up while the walk is inside a resolver. The walk read the
    // routes before, so it ends at the base it reached without the assembly, and remembers that in the routes it read:
    // the routes that replaced them have walked nothing, and the next dispatch reaches the type's own handler.
    public async Task ObjectRequest_WalkingAsAnAssemblyDeclaresItsType_AnswersFromTheRoutesItRead()
    {
        var gate = new Gate();
        MessageRoutes.AddResolvers(HoldingAt<StaleWalk>(gate), NoRoute, null, NoRoute);
        var mediator = Mediator(
            static x => x.AddHandler<StaleWalkBaseHandler>(),
            static services => services.AddTransient<IRequestHandler<StaleWalk, string>, TypeNameHandler<StaleWalk>>());

        var answer = await Race(
            gate,
            async () => await mediator.Request((object)new StaleWalk()),
            static () => MessageRoutes.AddResolvers(Declaring<StaleWalk>, NoRoute, null, NoRoute));

        Assert.Equal("base", answer);
        Assert.Null(MessageRoutes.WalkedRequests.Find(typeof(StaleWalk)));
        Assert.Equal(nameof(StaleWalk), await mediator.Request((object)new StaleWalk()));
    }

    [Fact]
    // As for a request, through the walk that keeps to one item type.
    public async Task InterfaceStream_WalkingAsAnAssemblyDeclaresItsType_AnswersFromTheRoutesItRead()
    {
        var gate = new Gate();
        MessageRoutes.AddResolvers(NoRoute, NoRoute, null, HoldingAt<StaleStream>(gate));
        var mediator = Mediator(
            static x => x.AddHandler<StaleStreamBaseHandler>(),
            static services =>
                services.AddTransient<IStreamRequestHandler<StaleStream, string>, TypeNameStreamHandler<StaleStream>>());

        var answer = await Race(gate,
            () => Collect(mediator.RequestStream(new StaleStream())),
            static () => MessageRoutes.AddResolvers(NoRoute, NoRoute, null, DeclaringStream<StaleStream>));

        Assert.Equal(["base"], answer);
        Assert.Null(MessageRoutes.WalkedStreams.Find(typeof(StaleStream)));
        Assert.Equal([nameof(StaleStream)], await Collect(mediator.RequestStream(new StaleStream())));
    }

    // ---- events -----------------------------------------------------------------------------------------------------

    [Fact]
    // An assembly declares a base level of an event while a publish is looking its levels up. The publish read the
    // routes before, so it reaches the event's own level alone, as it would have without the assembly. That is the
    // answer of routes that are gone: it stays out of the levels that replaced them, and the event is not recorded as
    // reaching its own level alone, so the next typed publish looks the levels up again and reaches the base level too.
    public async Task Publish_LookingUpLevelsAsAnAssemblyDeclaresABaseLevel_AnswersFromTheRoutesItRead()
    {
        var gate = new Gate();
        var log = new TraceLog();
        MessageRoutes.AddResolvers(NoRoute, HoldingAt<StaleLevelBase>(gate), null, NoRoute);
        var mediator = Mediator(
            static x => x.AddHandler<StaleLevelHandler>(),
            services => services.AddSingleton(log)
                .AddTransient<IEventHandler<StaleLevelBase>, TypeNameEventHandler<StaleLevelBase>>());

        await Race(
            gate,
            () => mediator.Publish(new StaleLevel()).AsTask(),
            static () => MessageRoutes.AddResolvers(NoRoute, DeclaringEvent<StaleLevelBase>, null, NoRoute));

        Assert.Equal(["own"], log.Entries);
        Assert.Null(MessageRoutes.EventLevels.Find(typeof(StaleLevel)));
        Assert.False(MessageRoutes.IsComplete<StaleLevel>());

        await mediator.Publish(new StaleLevel());

        Assert.Equal(["own", "own", nameof(StaleLevelBase)], log.Entries);
    }

    [Fact]
    // A typed publish that finds an event reaching its own level alone records that, and later typed publishes go
    // straight to that level. One held at an interface matcher has read the routes and found the event's own level; an
    // assembly then declares an interface level of the event. The publish finishes on the levels it found, but records
    // nothing, since the routes it read are gone, so the next typed publish reaches the interface level too.
    public async Task Publish_FindingAnEventCompleteAsAnAssemblyAddsALevel_DoesNotRecordIt()
    {
        var gate = new Gate();
        var log = new TraceLog();

        MessageRoutes.AddResolvers(NoRoute, NoRoute, (message, _) =>
        {
            if (message is CompleteRace) gate.Hold();
        }, NoRoute);

        var mediator = Mediator(
            static x => x.AddHandler<CompleteRaceHandler>(),
            services => services.AddSingleton(log)
                .AddTransient<IEventHandler<ICompleteRace>, TypeNameEventHandler<ICompleteRace>>());

        await Race(
            gate,
            () => mediator.Publish(new CompleteRace()).AsTask(),
            static () => MessageRoutes.AddResolvers(NoRoute, DeclaringEvent<ICompleteRace>, static (message, types) =>
            {
                if (message is ICompleteRace) types.Add(typeof(ICompleteRace));
            }, NoRoute));

        Assert.Equal(["own"], log.Entries);
        Assert.False(MessageRoutes.IsComplete<CompleteRace>());

        await mediator.Publish(new CompleteRace());

        Assert.Equal(["own", "own", nameof(ICompleteRace)], log.Entries);
    }

    // ---- one set of routes per dispatch -----------------------------------------------------------------------------

    [Fact]
    // The routes before an assembly send this request to its farthest base, and the routes after it to its own type. An
    // assembly that declares the request's own type and its nearer base, while the walk is inside a resolver asking
    // about the own type, leaves the walk to go on from there. Asking the new resolvers about the nearer base would end
    // the walk at a handler that neither set of routes chooses, so the walk keeps to the routes it began with.
    public async Task InterfaceRequest_WalkingAsAnAssemblyDeclaresNearerTypes_KeepsToTheRoutesItBeganWith()
    {
        var gate = new Gate();
        MessageRoutes.AddResolvers(HoldingAt<MixedWalkNear>(gate), NoRoute, null, NoRoute);

        var mediator = Mediator(
            static x => x.AddHandler<MixedWalkFarHandler>(),
            static services => services
                .AddTransient<IRequestHandler<MixedWalkMiddle, string>, TypeNameHandler<MixedWalkMiddle>>()
                .AddTransient<IRequestHandler<MixedWalkNear, string>, TypeNameHandler<MixedWalkNear>>());

        var answer = await Race(
            gate,
            async () => await mediator.Request(new MixedWalkNear()),
            static () => MessageRoutes.AddResolvers(
                static type => Declaring<MixedWalkNear>(type) ?? Declaring<MixedWalkMiddle>(type), NoRoute, null, NoRoute));

        Assert.Equal(nameof(MixedWalkFar), answer);
        Assert.Null(MessageRoutes.WalkedRequests.Find(typeof(MixedWalkNear)));
        Assert.Null(MessageRoutes.Current.Requests.Find(typeof(MixedWalkFar)));
        Assert.Equal(nameof(MixedWalkNear), await mediator.Request(new MixedWalkNear()));
    }

    [Fact]
    // As for a request, through the walk of a stream of any item type.
    public async Task ObjectStream_WalkingAsAnAssemblyDeclaresNearerTypes_KeepsToTheRoutesItBeganWith()
    {
        var gate = new Gate();
        MessageRoutes.AddResolvers(NoRoute, NoRoute, null, HoldingAt<MixedStreamNear>(gate));

        var mediator = Mediator(
            static x => x.AddHandler<MixedStreamFarHandler>(),
            static services => services
                .AddTransient<IStreamRequestHandler<MixedStreamMiddle, string>, TypeNameStreamHandler<MixedStreamMiddle>>()
                .AddTransient<IStreamRequestHandler<MixedStreamNear, string>, TypeNameStreamHandler<MixedStreamNear>>());

        var answer = await Race(gate,
            () => Collect(mediator.RequestStream((object)new MixedStreamNear())),
            static () => MessageRoutes.AddResolvers(
                NoRoute, NoRoute, null,
                static type => DeclaringStream<MixedStreamNear>(type) ?? DeclaringStream<MixedStreamMiddle>(type)));

        Assert.Equal([nameof(MixedStreamFar)], answer);
        Assert.Null(MessageRoutes.WalkedStreams.Find(typeof(MixedStreamNear)));
        Assert.Null(MessageRoutes.Current.Streams.Find(typeof(MixedStreamFar)));
        Assert.Equal([nameof(MixedStreamNear)], await Collect(mediator.RequestStream((object)new MixedStreamNear())));
    }

    [Fact]
    // An event's levels are its own type, its base classes and its interfaces, looked up in that order. An assembly
    // that declares a base level and an interface level of the event while the lookup is inside a resolver asking about
    // the base adds neither to this publish: taking the interface level from the new routes after the base from the old
    // ones would reach a set of handlers that neither set of routes chooses.
    public async Task Publish_LookingUpLevelsAsAnAssemblyDeclaresMore_KeepsToTheRoutesItBeganWith()
    {
        var gate = new Gate();
        var log = new TraceLog();
        MessageRoutes.AddResolvers(NoRoute, HoldingAt<MixedLevelBase>(gate), null, NoRoute);

        var mediator = Mediator(
            static x => x.AddHandler<MixedLevelHandler>(),
            services => services.AddSingleton(log)
                .AddTransient<IEventHandler<MixedLevelBase>, TypeNameEventHandler<MixedLevelBase>>()
                .AddTransient<IEventHandler<IMixedLevel>, TypeNameEventHandler<IMixedLevel>>());

        await Race(gate,
            () => mediator.Publish(new MixedLevel()).AsTask(),
            static () => MessageRoutes.AddResolvers(
                NoRoute, static type => DeclaringEvent<MixedLevelBase>(type) ?? DeclaringEvent<IMixedLevel>(type),
                static (message, types) =>
                {
                    if (message is IMixedLevel) types.Add(typeof(IMixedLevel));
                }, NoRoute));

        Assert.Equal(["own"], log.Entries);
        Assert.Null(MessageRoutes.EventLevels.Find(typeof(MixedLevel)));
        Assert.Null(MessageRoutes.Current.Events.Find(typeof(MixedLevel)));

        await mediator.Publish(new MixedLevel());

        Assert.Equal(["own", "own", nameof(MixedLevelBase), nameof(IMixedLevel)], log.Entries);
    }

    // ---- the race ---------------------------------------------------------------------------------------------------

    // Runs the dispatch on a thread of its own, since a resolver cannot yield and the gate holds the thread that
    // reaches it; once the gate holds it, initializes the assembly and opens the gate.
    private static async Task<T> Race<T>(Gate gate, Func<Task<T>> dispatch, Action initializeAssembly)
    {
        var held = Task.Factory.StartNew(dispatch, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        try
        {
            var first = await Task.WhenAny(gate.Reached, held);
            if (first == held)
            {
                await held;

                Assert.Fail("The dispatch finished without reaching the gate.");
            }

            await first;
            initializeAssembly();
        }
        finally
        {
            gate.Open();
        }

        return await held;
    }

    [SuppressMessage("Performance", "CA1859",
        Justification = "Match the routing callback or result-free race helper contract used by this test.")]
    private static Task Race(Gate gate, Func<Task> dispatch, Action initializeAssembly) =>
        Race(gate, async () =>
        {
            await dispatch();
            return true;
        }, initializeAssembly);

    // A resolver that declares nothing and holds, at its gate, the dispatch that first asks it about TMessage.
    private static Func<Type, object?> HoldingAt<TMessage>(Gate gate) => type =>
    {
        if (type == typeof(TMessage)) gate.Hold();

        return null;
    };

    private static object? NoRoute(Type type) => null;

    [SuppressMessage("Performance", "CA1859",
        Justification = "Match the routing callback or result-free race helper contract used by this test.")]
    private static object? Declaring<TRequest>(Type type) where TRequest : IRequest<string> =>
        type == typeof(TRequest) ? new ResponseRequestRoute<TRequest, string>() : null;

    [SuppressMessage("Performance", "CA1859",
        Justification = "Match the routing callback or result-free race helper contract used by this test.")]
    private static object? DeclaringStream<TRequest>(Type type) where TRequest : IStreamRequest<string> =>
        type == typeof(TRequest) ? new StreamRoute<TRequest, string>() : null;

    [SuppressMessage("Performance", "CA1859",
        Justification = "Match the routing callback or result-free race helper contract used by this test.")]
    private static object? DeclaringEvent<TEvent>(Type type) where TEvent : IEvent =>
        type == typeof(TEvent) ? new EventRoute<TEvent>() : null;

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        List<T> items = [];
        await foreach (var item in stream) items.Add(item);

        return items;
    }

    // Holds the first dispatch that reaches it until the test opens it. The bound only ends a test that has already
    // failed; nothing here is timed.
    private sealed class Gate
    {
        private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task.WaitAsync(Bound);

        public void Hold()
        {
            if (_reached.TrySetResult() && !_opened.Task.Wait(Bound))
                throw new TimeoutException("The test never opened the gate.");
        }

        public void Open() => _opened.TrySetResult();
    }
}

// Generic, so the generator declares no route for what they handle: the tests declare it, as an assembly
// initialized later would.
public sealed class TypeNameHandler<TRequest> : IRequestHandler<TRequest, string> where TRequest : IRequest<string>
{
    public ValueTask<string> Handle(TRequest message, CancellationToken cancellationToken) => new(typeof(TRequest).Name);
}

public sealed class TypeNameStreamHandler<TRequest> : IStreamRequestHandler<TRequest, string>
    where TRequest : IStreamRequest<string>
{
    public IAsyncEnumerable<string> Handle(TRequest message, CancellationToken cancellationToken) =>
        new[] { typeof(TRequest).Name }.ToAsyncEnumerable();
}

public sealed class TypeNameEventHandler<TEvent>(TraceLog log) : IEventHandler<TEvent> where TEvent : IEvent
{
    public ValueTask Handle(TEvent message, CancellationToken cancellationToken)
    {
        log.Add(typeof(TEvent).Name);
        return default;
    }
}

public record StaleWalkBase : IRequest<string>;

public sealed record StaleWalk : StaleWalkBase;

public sealed class StaleWalkBaseHandler : IRequestHandler<StaleWalkBase, string>
{
    public ValueTask<string> Handle(StaleWalkBase message, CancellationToken cancellationToken) => new("base");
}

public record StaleStreamBase : IStreamRequest<string>;

public sealed record StaleStream : StaleStreamBase;

public sealed class StaleStreamBaseHandler : IStreamRequestHandler<StaleStreamBase, string>
{
    private static readonly string[] SourceArray = ["base"];

    public IAsyncEnumerable<string> Handle(StaleStreamBase message, CancellationToken cancellationToken) => SourceArray.ToAsyncEnumerable();
}

public record StaleLevelBase : IEvent;

public sealed record StaleLevel : StaleLevelBase;

public sealed class StaleLevelHandler(TraceLog log) : IEventHandler<StaleLevel>
{
    public ValueTask Handle(StaleLevel message, CancellationToken cancellationToken)
    {
        log.Add("own");
        return default;
    }
}

public record MixedWalkFar : IRequest<string>;

public record MixedWalkMiddle : MixedWalkFar;

public sealed record MixedWalkNear : MixedWalkMiddle;

public sealed class MixedWalkFarHandler : IRequestHandler<MixedWalkFar, string>
{
    public ValueTask<string> Handle(MixedWalkFar message, CancellationToken cancellationToken) => new(nameof(MixedWalkFar));
}

public record MixedStreamFar : IStreamRequest<string>;

public record MixedStreamMiddle : MixedStreamFar;

public sealed record MixedStreamNear : MixedStreamMiddle;

public sealed class MixedStreamFarHandler : IStreamRequestHandler<MixedStreamFar, string>
{
    public IAsyncEnumerable<string> Handle(MixedStreamFar message, CancellationToken cancellationToken) =>
        new[] { nameof(MixedStreamFar) }.ToAsyncEnumerable();
}

public interface IMixedLevel : IEvent;

public record MixedLevelBase : IEvent;

public sealed record MixedLevel : MixedLevelBase, IMixedLevel;

public sealed class MixedLevelHandler(TraceLog log) : IEventHandler<MixedLevel>
{
    public ValueTask Handle(MixedLevel message, CancellationToken cancellationToken)
    {
        log.Add("own");
        return default;
    }
}

public interface ICompleteRace : IEvent;

public sealed record CompleteRace : ICompleteRace;

public sealed class CompleteRaceHandler(TraceLog log) : IEventHandler<CompleteRace>
{
    public ValueTask Handle(CompleteRace message, CancellationToken cancellationToken)
    {
        log.Add("own");
        return default;
    }
}