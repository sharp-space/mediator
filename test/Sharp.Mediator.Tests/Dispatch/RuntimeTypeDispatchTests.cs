using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator.Tests.Dispatch;

// Dispatch follows what a message is. Every way of calling the mediator reaches the same handler: that of the nearest
// type in the message's class chain that declares one. CI runs these with interceptors on and off, and each lifetime is
// checked cold and warm. The messages are public so that interceptors can name them, and no other test declares
// handlers for them, because routes are process-wide.
public sealed class RuntimeTypeDispatchTests
{
    // More rounds than a Scoped mediator misses before it caches, so the warm path answers too.
    private const int Rounds = 10;

    public static TheoryData<ServiceLifetime> Lifetimes => [ServiceLifetime.Singleton, ServiceLifetime.Scoped, ServiceLifetime.Transient];

    private static IMediator Mediator(ServiceLifetime lifetime, Action<MediatorBuilder> configure, RuntimeDispatchLog? log = null)
    {
        var services = new ServiceCollection();
        if (log is not null) services.AddSingleton(log);

        services.AddMediator(x => configure(x.WithLifetime(lifetime)));
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
    }

    private static IMediator Shapes(ServiceLifetime lifetime) =>
        Mediator(lifetime, static x => x.AddHandler<RuntimeShapeHandler>().AddHandler<RuntimeSquareHandler>());

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        List<T> items = [];
        await foreach (var item in stream) items.Add(item);

        return items;
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    public async Task EveryCallShape_ReachesTheNearestDeclaredHandler(ServiceLifetime lifetime)
    {
        var mediator = Shapes(lifetime);
        for (var round = 0; round < Rounds; round++)
        {
            Assert.Equal("square", await mediator.Request<RuntimeShape, string>(new RuntimeSquare()));
            Assert.Equal("square", await mediator.Request<RuntimeShape, string>(new RuntimeUnitSquare()));
            Assert.Equal("square", await mediator.Request<RuntimeSquare, string>(new RuntimeUnitSquare()));
            Assert.Equal("square", await mediator.Request<RuntimeUnitSquare, string>(new RuntimeUnitSquare()));
            Assert.Equal("square", await mediator.Request<IRequest<string>, string>(new RuntimeUnitSquare()));
            Assert.Equal("square", await mediator.Request(new RuntimeUnitSquare()));
            Assert.Equal("square", await mediator.Request((object)new RuntimeUnitSquare()));
            Assert.Equal("shape", await mediator.Request<RuntimeShape, string>(new RuntimeShape()));
            Assert.Equal("shape", await mediator.Request((object)new RuntimeShape()));
        }
    }

    [Fact]
    // Dispatching the base itself memoizes the base's route. That must not change where a derived message goes.
    public async Task Result_DoesNotDependOnWhatWasDispatchedBefore()
    {
        static IMediator Container() => Mediator(
            ServiceLifetime.Singleton, static x => x.AddHandler<RuntimeRoundHandler>().AddHandler<RuntimeOvalHandler>());

        var first = Container();
        Assert.Equal("oval", await first.Request<RuntimeRound, string>(new RuntimeOval()));
        Assert.Equal("round", await first.Request((object)new RuntimeRound()));
        Assert.Equal("oval", await Container().Request<RuntimeRound, string>(new RuntimeOval()));
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    // The nearest declared type decides even when this container did not register its handler: a missing registration
    // is reported rather than answered by a base class's handler.
    public async Task UnregisteredNearestHandler_Throws(ServiceLifetime lifetime)
    {
        var mediator = Mediator(lifetime, static x => x.AddHandler<RuntimeShapeHandler>());
        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await mediator.Request<RuntimeShape, string>(new RuntimeUnitSquare()));

        Assert.Contains(nameof(RuntimeSquare), exception.Message);
        Assert.Equal("shape", await mediator.Request<RuntimeShape, string>(new RuntimeShape()));
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    public async Task VoidRequest_ReachesTheNearestDeclaredHandler(ServiceLifetime lifetime)
    {
        var log = new RuntimeDispatchLog();
        var mediator = Mediator(lifetime, static x => x.AddHandler<RuntimeStepHandler>().AddHandler<RuntimeJumpHandler>(), log);
        for (var round = 0; round < Rounds; round++)
        {
            await mediator.Request<RuntimeStep>(new RuntimeLongJump());
            await mediator.Request(new RuntimeLongJump());
            await mediator.Request(new RuntimeStep());
        }

        Assert.Equal(2 * Rounds, log.Count("jump"));
        Assert.Equal(Rounds, log.Count("step"));
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    public async Task StreamRequest_ReachesTheNearestDeclaredHandler(ServiceLifetime lifetime)
    {
        var mediator = Mediator(
            lifetime, static x => x.AddHandler<RuntimeRangeHandler>().AddHandler<RuntimeEvenRangeHandler>());

        for (var round = 0; round < Rounds; round++)
        {
            Assert.Equal([2], await Collect(mediator.RequestStream<RuntimeRange, int>(new RuntimeFirstEvenRange())));
            Assert.Equal([2], await Collect(mediator.RequestStream(new RuntimeFirstEvenRange())));
            Assert.Equal([1], await Collect(mediator.RequestStream<RuntimeRange, int>(new RuntimeRange())));
        }
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    // A value type is exactly its static type, so its typed calls look up only its own route and never walk.
    public async Task ValueTypeMessages_ReachTheirHandlers(ServiceLifetime lifetime)
    {
        var log = new RuntimeDispatchLog();
        var mediator = Mediator(lifetime,
            static x => x.AddHandler<RuntimePointHandler>().AddHandler<RuntimeTickHandler>()
                .AddHandler<RuntimePixelsHandler>(), log);

        for (var round = 0; round < Rounds; round++)
        {
            Assert.Equal(3, await mediator.Request<RuntimePoint, int>(new RuntimePoint(3)));

            await mediator.Request(new RuntimeTick());
            Assert.Equal([5], await Collect(mediator.RequestStream<RuntimePixels, int>(new RuntimePixels(5))));
        }

        Assert.Equal(Rounds, log.Count("tick"));
    }

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [Fact]
    public void UndeclaredMessage_ThrowsFromEveryTypedEntryPoint()
    {
        var mediator = Shapes(ServiceLifetime.Singleton);
        Assert.Throws<InvalidOperationException>(() => { _ = mediator.Request<RuntimeOrphan, string>(new RuntimeOrphan()); });
        Assert.Throws<InvalidOperationException>(() => { _ = mediator.Request(new RuntimeOrphanCommand()); });
        Assert.Throws<InvalidOperationException>(() => mediator.RequestStream<RuntimeOrphanStream, int>(new RuntimeOrphanStream()));
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    // A handler the generator never declared, such as a closed generic one registered by hand, answers no call, whether
    // the message is a class or a value type.
    public void HandlerRegisteredByHand_IsNotReached()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<RuntimeByHand, int>, RuntimeByHandHandler<RuntimeByHand>>();
        services.AddTransient<IRequestHandler<RuntimeByHandPoint, int>, RuntimeByHandHandler<RuntimeByHandPoint>>();
        services.AddTransient<IRequestHandler<RuntimeByHandTick>, RuntimeByHandVoidHandler<RuntimeByHandTick>>();
        services.AddTransient<IStreamRequestHandler<RuntimeByHandPixels, int>, RuntimeByHandStreamHandler<RuntimeByHandPixels>>();
        services.AddMediator(static _ => { });
        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        Assert.Throws<InvalidOperationException>(() => { _ = mediator.Request<RuntimeByHand, int>(new RuntimeByHand()); });
        Assert.Throws<InvalidOperationException>(() => { _ = mediator.Request<RuntimeByHandPoint, int>(new RuntimeByHandPoint()); });
        Assert.Throws<InvalidOperationException>(() => { _ = mediator.Request(new RuntimeByHandTick()); });
        Assert.Throws<InvalidOperationException>(() => mediator.RequestStream<RuntimeByHandPixels, int>(new RuntimeByHandPixels()));
    }
}

/// <summary>Records what handlers saw, for the messages above that return nothing.</summary>
public sealed class RuntimeDispatchLog
{
    private readonly List<string> _entries = [];

    public void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    public int Count(string entry)
    {
        lock (_entries)
        {
            return _entries.Count(x => x == entry);
        }
    }
}

public record RuntimeShape : IRequest<string>;

public record RuntimeSquare : RuntimeShape;

public sealed record RuntimeUnitSquare : RuntimeSquare;

public sealed class RuntimeShapeHandler : IRequestHandler<RuntimeShape, string>
{
    public ValueTask<string> Handle(RuntimeShape message, CancellationToken cancellationToken) => new("shape");
}

public sealed class RuntimeSquareHandler : IRequestHandler<RuntimeSquare, string>
{
    public ValueTask<string> Handle(RuntimeSquare message, CancellationToken cancellationToken) => new("square");
}

public record RuntimeRound : IRequest<string>;

public sealed record RuntimeOval : RuntimeRound;

public sealed class RuntimeRoundHandler : IRequestHandler<RuntimeRound, string>
{
    public ValueTask<string> Handle(RuntimeRound message, CancellationToken cancellationToken) => new("round");
}

public sealed class RuntimeOvalHandler : IRequestHandler<RuntimeOval, string>
{
    public ValueTask<string> Handle(RuntimeOval message, CancellationToken cancellationToken) => new("oval");
}

public record RuntimeStep : IRequest;

public record RuntimeJump : RuntimeStep;

public sealed record RuntimeLongJump : RuntimeJump;

public sealed class RuntimeStepHandler(RuntimeDispatchLog log) : IRequestHandler<RuntimeStep>
{
    public ValueTask Handle(RuntimeStep message, CancellationToken cancellationToken)
    {
        log.Add("step");
        return default;
    }
}

public sealed class RuntimeJumpHandler(RuntimeDispatchLog log) : IRequestHandler<RuntimeJump>
{
    public ValueTask Handle(RuntimeJump message, CancellationToken cancellationToken)
    {
        log.Add("jump");
        return default;
    }
}

public record RuntimeRange : IStreamRequest<int>;

public record RuntimeEvenRange : RuntimeRange;

public sealed record RuntimeFirstEvenRange : RuntimeEvenRange;

public sealed class RuntimeRangeHandler : IStreamRequestHandler<RuntimeRange, int>
{
    public IAsyncEnumerable<int> Handle(RuntimeRange message, CancellationToken cancellationToken) => new[] { 1 }.ToAsyncEnumerable();
}

public sealed class RuntimeEvenRangeHandler : IStreamRequestHandler<RuntimeEvenRange, int>
{
    public IAsyncEnumerable<int> Handle(RuntimeEvenRange message, CancellationToken cancellationToken) => new[] { 2 }.ToAsyncEnumerable();
}

public readonly record struct RuntimePoint(int X) : IRequest<int>;

public sealed class RuntimePointHandler : IRequestHandler<RuntimePoint, int>
{
    public ValueTask<int> Handle(RuntimePoint message, CancellationToken cancellationToken) => new(message.X);
}

public readonly record struct RuntimeTick : IRequest;

public sealed class RuntimeTickHandler(RuntimeDispatchLog log) : IRequestHandler<RuntimeTick>
{
    public ValueTask Handle(RuntimeTick message, CancellationToken cancellationToken)
    {
        log.Add("tick");
        return default;
    }
}

public readonly record struct RuntimePixels(int Count) : IStreamRequest<int>;

public sealed class RuntimePixelsHandler : IStreamRequestHandler<RuntimePixels, int>
{
    public IAsyncEnumerable<int> Handle(RuntimePixels message, CancellationToken cancellationToken) =>
        new[] { message.Count }.ToAsyncEnumerable();
}

public sealed record RuntimeByHand : IRequest<int>;

public readonly record struct RuntimeByHandPoint : IRequest<int>;

public readonly record struct RuntimeByHandTick : IRequest;

public readonly record struct RuntimeByHandPixels : IStreamRequest<int>;

// Generic, so the generator declares no route for what they handle.
public sealed class RuntimeByHandHandler<TRequest> : IRequestHandler<TRequest, int> where TRequest : IRequest<int>
{
    public ValueTask<int> Handle(TRequest message, CancellationToken cancellationToken) => new(1);
}

public sealed class RuntimeByHandVoidHandler<TRequest> : IRequestHandler<TRequest> where TRequest : IRequest
{
    public ValueTask Handle(TRequest message, CancellationToken cancellationToken) => default;
}

public sealed class RuntimeByHandStreamHandler<TRequest> : IStreamRequestHandler<TRequest, int> where TRequest : IStreamRequest<int>
{
    public IAsyncEnumerable<int> Handle(TRequest message, CancellationToken cancellationToken) => new[] { 1 }.ToAsyncEnumerable();
}

public sealed record RuntimeOrphan : IRequest<string>;

public sealed record RuntimeOrphanCommand : IRequest;

public sealed record RuntimeOrphanStream : IStreamRequest<int>;