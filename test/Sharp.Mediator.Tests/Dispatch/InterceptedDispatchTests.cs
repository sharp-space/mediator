using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator.Tests.Dispatch;

// With interceptors on, every call below goes through MediatorCalls; the messages are public because the generator
// skips a type it cannot name. An interceptor may only be faster than the interface call it replaces, so these pin
// the cases where the static type at the call site does not decide the handler. CI also runs them with interceptors
// off. Routes are process-wide, so a message with a handler of its own in one test cannot stand in for an inherited
// one in another.
public sealed class InterceptedDispatchTests
{
    private static ServiceProvider Provider(Action<MediatorBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddMediator(x => configure(x.WithLifetime(ServiceLifetime.Singleton)));
        return services.BuildServiceProvider();
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        List<T> items = [];
        await foreach (var item in stream) items.Add(item);

        return items;
    }

    [Fact]
    public async Task InferredCall_WithASealedArgument_ReachesTheHandlerOfItsBaseClass()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedBaseHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        Assert.Equal("base:1", await mediator.Request(new InterceptedSealed(1)));
        Assert.Equal("base:2", await mediator.Request(new InterceptedSealed(2)));
    }

    [Fact]
    public async Task InferredCall_WithABaseStaticType_DispatchesOnTheRuntimeType()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedBaseHandler>().AddHandler<InterceptedMoreDerivedHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        InterceptedBase message = new InterceptedMoreDerived(3);
        Assert.Equal("derived:3", await mediator.Request(message));
        Assert.Equal("derived:3", await mediator.Request(message));
        Assert.Equal("base:3", await mediator.Request(new InterceptedBase(3)));
    }

    [Fact]
    public async Task InferredCall_WarmSlot_DoesNotCaptureADerivedArgument()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedBaseHandler>().AddHandler<InterceptedMoreDerivedHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        await mediator.Request(new InterceptedBase(0)); // fills the slot of the static type
        await mediator.Request(new InterceptedBase(0));
        InterceptedBase derived = new InterceptedMoreDerived(4);
        Assert.Equal("derived:4", await mediator.Request(derived));
    }

    [Fact]
    // Each null test passes an open message and a sealed one, whose chain is taken without checking the runtime type.
    public async Task InferredCall_ForANullArgument_ThrowsWhatTheInterfaceThrows()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedBaseHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        InterceptedBase? open = null;
        InterceptedSealed? exact = null;

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(async () => await mediator.Request(open!));
        Assert.Equal("message", exception.ParamName);

        exception = await Assert.ThrowsAsync<ArgumentNullException>(async () => await mediator.Request(exact!));
        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public async Task TypedCall_ForANullArgument_ThrowsWhatTheInterfaceThrows()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedBaseHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(async () => await mediator.Request<InterceptedBase, string>(null!));
        Assert.Equal("message", exception.ParamName);

        exception = await Assert.ThrowsAsync<ArgumentNullException>(async () => await mediator.Request<InterceptedSealed, string>(null!));
        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public async Task VoidCall_ForANullArgument_ThrowsWhatTheInterfaceThrows()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedVoidHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        InterceptedOpenVoid? open = null;
        InterceptedVoid? exact = null;

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(async () => await mediator.Request(open!));
        Assert.Equal("message", exception.ParamName);

        exception = await Assert.ThrowsAsync<ArgumentNullException>(async () => await mediator.Request(exact!));
        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public async Task InferredCall_WithoutAHandler_ThrowsWhatTheInterfaceThrows()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedBaseHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await mediator.Request(new InterceptedOrphan()));
    }

    [Fact]
    public async Task InferredStream_WithASealedArgument_ReachesTheHandlerOfItsBaseClass()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedStreamHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        Assert.Equal([7], await Collect(mediator.RequestStream(new InterceptedSealedStream())));
        Assert.Equal([7], await Collect(mediator.RequestStream(new InterceptedSealedStream())));
        Assert.Equal([7], await Collect(mediator.RequestStream(new InterceptedStreamBase())));
        Assert.Equal([7], await Collect(mediator.RequestStream(new InterceptedStreamBase())));
    }

    [Fact]
    public async Task InferredStream_WithABaseStaticType_DispatchesOnTheRuntimeType()
    {
        await using var provider = Provider(static x => x
            .AddHandler<InterceptedRootStreamHandler>().AddHandler<InterceptedLeafStreamHandler>());

        var mediator = provider.GetRequiredService<IMediator>();
        InterceptedRootStream message = new InterceptedLeafStream();
        Assert.Equal([8], await Collect(mediator.RequestStream(message)));
    }

    [Fact]
    public void InferredStream_ForANullArgument_ThrowsWhatTheInterfaceThrows()
    {
        using var provider = Provider(static x => x.AddHandler<InterceptedStreamHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        InterceptedStreamBase? open = null;
        InterceptedSealedStream? exact = null;

        Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => mediator.RequestStream(open!)).ParamName);
        Assert.Equal("message", Assert.Throws<ArgumentNullException>(() => mediator.RequestStream(exact!)).ParamName);
    }

    [Fact]
    public void TypedStream_ForANullArgument_ThrowsWhatTheInterfaceThrows()
    {
        using var provider = Provider(static x => x.AddHandler<InterceptedStreamHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        Assert.Equal("message",
            Assert.Throws<ArgumentNullException>(() =>
                mediator.RequestStream<InterceptedStreamBase, int>(null!)).ParamName);

        Assert.Equal("message",
            Assert.Throws<ArgumentNullException>(() =>
                mediator.RequestStream<InterceptedSealedStream, int>(null!)).ParamName);
    }

    [Fact]
    // A sealed event is published without its runtime type compared, and an open one with it; a null message fails the
    // same way through either.
    public async Task TypedPublish_ForANullArgument_ThrowsWhatTheInterfaceThrows()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedEventHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        Assert.Equal("message",
            (await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await mediator.Publish<InterceptedOpenEvent>(null!))).ParamName);

        Assert.Equal("message",
            (await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await mediator.Publish<InterceptedEvent>(null!))).ParamName);
    }

    [Fact]
    // A call naming IRequest<T> or IStreamRequest<T> itself still reaches the handler of what the message is: no
    // runtime type is an interface, so no handler can be declared for one.
    public async Task NamedCall_OnTheResponseInterface_ReachesTheMessagesHandler()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedQuestionHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        IRequest<InterceptedAnswer> message = new InterceptedQuestion();
        Assert.Equal(new InterceptedAnswer("question"),
            await mediator.Request<IRequest<InterceptedAnswer>, InterceptedAnswer>(message));

        Assert.Equal(new InterceptedAnswer("question"),
            await mediator.Request<IRequest<InterceptedAnswer>, InterceptedAnswer>(message));
    }

    [Fact]
    public async Task NamedStream_OnTheResponseInterface_ReachesTheMessagesHandler()
    {
        await using var provider = Provider(static x => x.AddHandler<InterceptedChunksHandler>());
        var mediator = provider.GetRequiredService<IMediator>();
        IStreamRequest<InterceptedChunk> message = new InterceptedChunks();
        Assert.Equal(
            [new InterceptedChunk("chunks")],
            await Collect(mediator.RequestStream<IStreamRequest<InterceptedChunk>, InterceptedChunk>(message)));
    }

    [Fact]
    // An expression tree records a call as written, and libraries that read one, such as a Moq setup, look for the
    // mediator's own method. A nested lambda converts to a delegate inside the tree, and a query clause is a lambda
    // of its own.
    public void CallsInsideExpressionTrees_StayOnTheInterface()
    {
        Expression<Func<IMediator, ValueTask<string>>>
            typed = m => m.Request<InterceptedBase, string>(new InterceptedBase(0), CancellationToken.None);

        Expression<Func<IMediator, Func<ValueTask<string>>>> nested = m => () => m.Request(new InterceptedSealed(0), CancellationToken.None);
        LambdaExpression untyped = (IMediator m) => m.Publish(new InterceptedEvent(), CancellationToken.None);
        var queried = from m in Array.Empty<IMediator>().AsQueryable() select m.RequestStream(new InterceptedStreamBase(), CancellationToken.None);

        Assert.Equal(typeof(IRequester), CalledMethod(typed).DeclaringType);
        Assert.Equal(typeof(IRequester), CalledMethod(nested).DeclaringType);
        Assert.Equal(typeof(IPublisher), CalledMethod(untyped).DeclaringType);
        Assert.Equal(typeof(IRequester), CalledMethod(((MethodCallExpression)queried.Expression).Arguments[1]).DeclaringType);
    }

    [Fact]
    // An interceptor takes its receiver as IPublisher or IRequester, which would box a struct, so a call that changes
    // the receiver would change a copy. A value-type receiver keeps the call as written.
    public void ValueTypeReceiver_IsCalledInPlace()
    {
        var publisher = new CountingPublisher();
        var published = PublishThrough(ref publisher);
        Assert.True(published.IsCompletedSuccessfully);
        Assert.Equal(1, publisher.Count);
    }

    private static ValueTask PublishThrough<TPublisher>(ref TPublisher publisher) where TPublisher : struct, IPublisher =>
        publisher.Publish(new InterceptedEvent());

    private static MethodInfo CalledMethod(Expression expression) => expression switch
    {
        LambdaExpression lambda => CalledMethod(lambda.Body),
        UnaryExpression quote => CalledMethod(quote.Operand),
        MethodCallExpression call => call.Method,
        _ => throw new ArgumentException($"No call in '{expression}'.", nameof(expression))
    };

    [Fact]
    // Every shape twice: with a sealed message, whose chain is taken or which is published without checking the runtime
    // type, and an open one.
    public async Task EveryCallShape_ReachesAnotherImplementation_Unchanged()
    {
        IMediator mediator = new RecordingMediator();
        var recording = (RecordingMediator)mediator;

        await mediator.Request(new InterceptedVoid());
        await mediator.Request(new InterceptedOpenVoid());
        await mediator.Request<InterceptedSealed, string>(new InterceptedSealed(1));
        await mediator.Request<InterceptedBase, string>(new InterceptedBase(1));
        await mediator.Request(new InterceptedSealed(1));
        await mediator.Request(new InterceptedBase(1));
        await mediator.Publish(new InterceptedEvent());
        await mediator.Publish(new InterceptedOpenEvent());

        await Collect(mediator.RequestStream<InterceptedSealedStream, int>(new InterceptedSealedStream()));
        await Collect(mediator.RequestStream<InterceptedStreamBase, int>(new InterceptedStreamBase()));
        await Collect(mediator.RequestStream(new InterceptedSealedStream()));
        await Collect(mediator.RequestStream(new InterceptedStreamBase()));

        Assert.Equal(
            [
                "void", "void", "typed", "typed", "inferred", "inferred", "event", "event", "typed-stream", "typed-stream",
                "inferred-stream", "inferred-stream"
            ],
            recording.Calls);
    }

    [Fact]
    // The interceptors find the mediator by comparing a receiver's exact type with it, which only matches
    // every instance of it while nothing can derive from it. Unsealed, a derived mediator would take the
    // uncached route unnoticed.
    public void DefaultMediator_IsSealed() => Assert.True(typeof(DefaultMediator).IsSealed);

    [Fact]
    // An interceptor compares the receiver's exact type before anything else, and reading the type of null throws what
    // the interface call on null throws, a null message included.
    public async Task EveryCallShape_OnANullReceiver_ThrowsWhatTheInterfaceThrows()
    {
        IMediator mediator = null!;
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Request<InterceptedSealed, string>(null!));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Request<InterceptedBase, string>(null!));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Request(new InterceptedVoid()));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Request(new InterceptedOpenVoid()));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Request<InterceptedSealed, string>(new InterceptedSealed(1)));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Request<InterceptedBase, string>(new InterceptedBase(1)));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Request(new InterceptedSealed(1)));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Request(new InterceptedBase(1)));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Publish(new InterceptedEvent()));
        await Assert.ThrowsAsync<NullReferenceException>(async () => await mediator.Publish(new InterceptedOpenEvent()));
        Assert.Throws<NullReferenceException>(() => mediator.RequestStream<InterceptedSealedStream, int>(new InterceptedSealedStream()));
        Assert.Throws<NullReferenceException>(() => mediator.RequestStream<InterceptedStreamBase, int>(new InterceptedStreamBase()));
        Assert.Throws<NullReferenceException>(() => mediator.RequestStream(new InterceptedSealedStream()));
        Assert.Throws<NullReferenceException>(() => mediator.RequestStream(new InterceptedStreamBase()));
    }

    [Fact]
    // A call from generic code, which no interceptor can name the types of, goes through a generic interceptor
    // that skips the interface's generic virtual call: it reaches the mediator as the interface call does, and
    // another implementation unchanged.
    public async Task EveryCallShape_FromGenericCode_ReachesTheMediator()
    {
        await using var provider = Provider(static x => x
            .AddHandler<InterceptedVoidHandler>()
            .AddHandler<InterceptedBaseHandler>()
            .AddHandler<InterceptedEventHandler>()
            .AddHandler<InterceptedStreamHandler>());

        var mediator = provider.GetRequiredService<IMediator>();
        await FromGenericCode.Void(mediator, new InterceptedVoid());
        Assert.Equal("base:1", await FromGenericCode.Typed<InterceptedBase, string>(mediator, new InterceptedBase(1)));
        Assert.Equal("base:2", await FromGenericCode.Typed<InterceptedSealed, string>(mediator, new InterceptedSealed(2)));

        await FromGenericCode.Publish(mediator, new InterceptedEvent());
        Assert.Equal([7], await Collect(FromGenericCode.Stream<InterceptedStreamBase, int>(mediator, new InterceptedStreamBase())));
    }

    [Fact]
    public async Task EveryCallShape_FromGenericCode_ReachesAnotherImplementation_Unchanged()
    {
        IMediator mediator = new RecordingMediator();
        var recording = (RecordingMediator)mediator;

        await FromGenericCode.Void(mediator, new InterceptedVoid());
        await FromGenericCode.Typed<InterceptedSealed, string>(mediator, new InterceptedSealed(1));
        await FromGenericCode.Inferred(mediator, new InterceptedWrapped<string>());
        await FromGenericCode.Publish(mediator, new InterceptedEvent());
        await Collect(FromGenericCode.Stream<InterceptedStreamBase, int>(mediator, new InterceptedStreamBase()));
        await Collect(FromGenericCode.InferredStream(mediator, new InterceptedFlow<int>()));

        Assert.Equal(["void", "typed", "inferred", "event", "typed-stream", "inferred-stream"], recording.Calls);
    }

    [Fact]
    public async Task EveryTypedShape_ReachesTheMediator()
    {
        await using var provider = Provider(static x => x
            .AddHandler<InterceptedVoidHandler>()
            .AddHandler<InterceptedBaseHandler>()
            .AddHandler<InterceptedEventHandler>()
            .AddHandler<InterceptedStreamHandler>());

        var mediator = provider.GetRequiredService<IMediator>();
        await mediator.Request(new InterceptedVoid());
        Assert.Equal("base:1", await mediator.Request<InterceptedBase, string>(new InterceptedBase(1)));

        await mediator.Publish(new InterceptedEvent());
        Assert.Equal([7], await Collect(mediator.RequestStream<InterceptedStreamBase, int>(new InterceptedStreamBase())));
    }

    private static class FromGenericCode
    {
        public static ValueTask Void<TRequest>(IMediator mediator, TRequest message) where TRequest : IRequest =>
            mediator.Request(message);

        public static ValueTask<TResponse> Typed<TRequest, TResponse>(IMediator mediator, TRequest message)
            where TRequest : IRequest<TResponse> => mediator.Request<TRequest, TResponse>(message);

        public static ValueTask<TResponse> Inferred<TResponse>(IMediator mediator, InterceptedWrapped<TResponse> message) =>
            mediator.Request(message);

        public static ValueTask Publish<TEvent>(IMediator mediator, TEvent message) where TEvent : IEvent =>
            mediator.Publish(message);

        public static IAsyncEnumerable<TResponse> Stream<TRequest, TResponse>(IMediator mediator, TRequest message)
            where TRequest : IStreamRequest<TResponse> =>
            mediator.RequestStream<TRequest, TResponse>(message);

        public static IAsyncEnumerable<TResponse> InferredStream<TResponse>(IMediator mediator, InterceptedFlow<TResponse> message) =>
            mediator.RequestStream(message);
    }

    private struct CountingPublisher : IPublisher
    {
        public int Count { get; private set; }

        public ValueTask Publish<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : IEvent
        {
            Count++;
            return default;
        }

        public ValueTask Publish<TEvent>(TEvent message, IEventPublisher? publisher, CancellationToken cancellationToken = default)
            where TEvent : IEvent => throw new NotSupportedException();

        public ValueTask Publish(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingMediator : IMediator
    {
        public List<string> Calls { get; } = [];

        public ValueTask Request<TRequest>(TRequest message, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Calls.Add("void");
            return default;
        }

        public ValueTask<TResponse> Request<TRequest, TResponse>(TRequest message, CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>
        {
            Calls.Add("typed");
            return new ValueTask<TResponse>(default(TResponse)!);
        }

        public ValueTask<TResponse> Request<TResponse>(IRequest<TResponse> message, CancellationToken cancellationToken = default)
        {
            Calls.Add("inferred");
            return new ValueTask<TResponse>(default(TResponse)!);
        }

        public ValueTask<object?> Request(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> RequestStream<TRequest, TResponse>(TRequest message, CancellationToken cancellationToken = default)
            where TRequest : IStreamRequest<TResponse>
        {
            Calls.Add("typed-stream");
            return AsyncEnumerable.Empty<TResponse>();
        }

        public IAsyncEnumerable<TResponse> RequestStream<TResponse>(IStreamRequest<TResponse> message, CancellationToken cancellationToken = default)
        {
            Calls.Add("inferred-stream");
            return AsyncEnumerable.Empty<TResponse>();
        }

        public IAsyncEnumerable<object?> RequestStream(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask Publish<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : IEvent
        {
            Calls.Add("event");
            return default;
        }

        public ValueTask Publish<TEvent>(TEvent message, IEventPublisher? publisher, CancellationToken cancellationToken = default)
            where TEvent : IEvent => throw new NotSupportedException();

        public ValueTask Publish(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

public record InterceptedBase(int Value) : IRequest<string>;

public sealed record InterceptedSealed(int Value) : InterceptedBase(Value);

public record InterceptedMoreDerived(int Value) : InterceptedBase(Value);

public sealed record InterceptedOrphan : IRequest<string>;

public sealed class InterceptedBaseHandler : IRequestHandler<InterceptedBase, string>
{
    public ValueTask<string> Handle(InterceptedBase message, CancellationToken cancellationToken) => new($"base:{message.Value}");
}

public sealed class InterceptedMoreDerivedHandler : IRequestHandler<InterceptedMoreDerived, string>
{
    public ValueTask<string> Handle(InterceptedMoreDerived message, CancellationToken cancellationToken) => new($"derived:{message.Value}");
}

public sealed record InterceptedVoid : IRequest;

public sealed record InterceptedWrapped<T> : IRequest<T>;

public sealed record InterceptedFlow<T> : IStreamRequest<T>;

public record InterceptedOpenVoid : IRequest;

public sealed class InterceptedVoidHandler : IRequestHandler<InterceptedVoid>
{
    public ValueTask Handle(InterceptedVoid message, CancellationToken cancellationToken) => default;
}

public sealed record InterceptedEvent : IEvent;

public record InterceptedOpenEvent : IEvent;

public sealed class InterceptedEventHandler : IEventHandler<InterceptedEvent>
{
    public ValueTask Handle(InterceptedEvent message, CancellationToken cancellationToken) => default;
}

public record InterceptedStreamBase : IStreamRequest<int>;

public sealed record InterceptedSealedStream : InterceptedStreamBase;

public sealed class InterceptedStreamHandler : IStreamRequestHandler<InterceptedStreamBase, int>
{
    public async IAsyncEnumerable<int> Handle(InterceptedStreamBase message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 7;
        await Task.CompletedTask;
    }
}

public record InterceptedRootStream : IStreamRequest<int>;

public sealed record InterceptedLeafStream : InterceptedRootStream;

public sealed class InterceptedRootStreamHandler : IStreamRequestHandler<InterceptedRootStream, int>
{
    public async IAsyncEnumerable<int> Handle(
        InterceptedRootStream message,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        yield return 7;
        await Task.CompletedTask;
    }
}

public sealed class InterceptedLeafStreamHandler : IStreamRequestHandler<InterceptedLeafStream, int>
{
    public async IAsyncEnumerable<int> Handle(InterceptedLeafStream message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 8;
        await Task.CompletedTask;
    }
}

public sealed record InterceptedAnswer(string Value);

public sealed record InterceptedQuestion : IRequest<InterceptedAnswer>;

public sealed class InterceptedQuestionHandler : IRequestHandler<InterceptedQuestion, InterceptedAnswer>
{
    public ValueTask<InterceptedAnswer> Handle(InterceptedQuestion message, CancellationToken cancellationToken) =>
        new(new InterceptedAnswer("question"));
}

public sealed record InterceptedChunk(string Value);

public sealed record InterceptedChunks : IStreamRequest<InterceptedChunk>;

public sealed class InterceptedChunksHandler : IStreamRequestHandler<InterceptedChunks, InterceptedChunk>
{
    public async IAsyncEnumerable<InterceptedChunk> Handle(
        InterceptedChunks message,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        yield return new InterceptedChunk("chunks");
        await Task.CompletedTask;
    }
}