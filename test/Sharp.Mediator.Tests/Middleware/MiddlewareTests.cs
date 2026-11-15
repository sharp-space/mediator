using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Fixtures;

namespace Sharp.Mediator.Tests.Middleware;

public sealed class MiddlewareTests
{
    [Fact]
    public async Task RequestMiddlewares_RunInRegistrationOrder_AroundHandler()
    {
        var log = new TraceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator(x => x
            .AddMiddleware<OuterEchoMiddleware>()
            .AddMiddleware<InnerEchoMiddleware>()
            .AddHandler<EchoRequestHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Request<EchoRequest, string>(new EchoRequest("payload"));
        Assert.Equal("echo:payload", result);

        Assert.Equal(
            ["outer:before", "inner:before", "inner:after", "outer:after"],
            log.Entries);
    }

    [Fact]
    public async Task RequestMiddleware_CanShortCircuit()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .AddMiddleware<ShortCircuitMiddleware>()
            .AddHandler<EchoRequestHandler>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Request<EchoRequest, string>(new EchoRequest("ignored"));
        Assert.Equal("short-circuit", result);
    }

    [Fact]
    public async Task OpenGenericMiddleware_AppliesToAllMessages()
    {
        var log = new TraceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator(x => x
            .AddMiddleware(typeof(LoggingMiddleware<,>))
            .AddHandler<EchoRequestHandler>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Request<EchoRequest, string>(new EchoRequest("hi"));

        Assert.Contains("logging:EchoRequest", log.Entries);
    }

    [Fact]
    // An open-generic middleware whose constraint a message meets only through variance applies to it, as C# accepts
    // the closed type: the generator left it out, without a diagnostic, while it matched constraint types by name.
    public async Task OpenGenericMiddleware_AppliesWhereAMessageMeetsItsConstraintThroughVariance()
    {
        var log = new TraceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator(static x => x.AddHandler<VarianceQueryHandler>().AddMiddleware(typeof(VarianceAudit<,>)));

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Request<VarianceQuery, List<string>>(new VarianceQuery());

        Assert.Equal(["audited"], log.Entries);
    }

    [Fact]
    public async Task ClosedConcreteMiddleware_Registered_AppliesToTargetMessage()
    {
        // Regression: closed (non-generic) middleware used to fail BuildServiceProvider.
        var log = new TraceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator(x => x
            .AddMiddleware<EchoOnlyMiddleware>()
            .AddHandler<EchoRequestHandler>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Request<EchoRequest, string>(new EchoRequest("hi"));

        Assert.Contains("echo-only", log.Entries);
    }

    [Fact]
    public async Task StreamMiddleware_WrapsHandler()
    {
        var log = new TraceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator(x => x
            .AddMiddleware<StreamLoggingMiddleware>()
            .AddHandler<StreamRequestHandler>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var items = new List<int>();
        await foreach (var item in mediator.RequestStream<StreamRequest, int>(new StreamRequest(3)))
            items.Add(item);

        Assert.Equal([0, 1, 2], items);
        Assert.Contains("stream:before", log.Entries);
        Assert.Contains("stream:after", log.Entries);
    }

    [Fact]
    public async Task OpenGenericStreamMiddleware_AppliesToAllStreams()
    {
        // Regression: open-generic stream middleware used to fail registration.
        var log = new TraceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator(x => x
            .AddMiddleware(typeof(OpenStreamMiddleware<,>))
            .AddHandler<StreamRequestHandler>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var items = new List<int>();
        await foreach (var item in mediator.RequestStream<StreamRequest, int>(new StreamRequest(2)))
            items.Add(item);

        Assert.Equal([0, 1], items);
        Assert.Contains("open-stream:StreamRequest", log.Entries);
    }

    [Fact]
    public async Task EventMiddleware_WrapsEachHandler()
    {
        var log = new TraceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator(x => x
            .AddMiddleware(typeof(EventLoggingMiddleware<>))
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Publish(new TestEvent("t"));

        // One wrap per handler.
        Assert.Equal(2, log.Entries.Count(x => x.StartsWith("event-mw:")));
    }

    [Fact]
    public async Task BuildEach_MakesOneChainPerHandler()
    {
        var log = new TraceLog();
        IEventHandler<TestEvent>[] handlers = [new FirstEventHandler(log), new SecondEventHandler(log)];

        var chains = MessageHandleBuilder.BuildEach(handlers, []);
        Assert.Equal(2, chains.Length);

        await chains[0](new TestEvent("f"), CancellationToken.None);
        await chains[1](new TestEvent("s"), CancellationToken.None);

        Assert.Equal(["first:f", "second:s"], log.Entries);
    }

    internal sealed class OuterEchoMiddleware(TraceLog log) : IMiddleware<EchoRequest, string>
    {
        public async ValueTask<string> Handle(
            EchoRequest message,
            MessageHandle<EchoRequest, string> next,
            CancellationToken cancellationToken
        )
        {
            log.Add("outer:before");

            try
            {
                return await next(message, cancellationToken);
            }
            finally
            {
                log.Add("outer:after");
            }
        }
    }

    internal sealed class InnerEchoMiddleware(TraceLog log) : IMiddleware<EchoRequest, string>
    {
        public async ValueTask<string> Handle(
            EchoRequest message,
            MessageHandle<EchoRequest, string> next,
            CancellationToken cancellationToken
        )
        {
            log.Add("inner:before");

            try
            {
                return await next(message, cancellationToken);
            }
            finally
            {
                log.Add("inner:after");
            }
        }
    }

    internal sealed class ShortCircuitMiddleware : IMiddleware<EchoRequest, string>
    {
        public ValueTask<string> Handle(
            EchoRequest message,
            MessageHandle<EchoRequest, string> next,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult("short-circuit");
    }

    internal sealed class LoggingMiddleware<TMessage, TResponse>(TraceLog log) : IMiddleware<TMessage, TResponse>
        where TMessage : IMessage<TResponse>
    {
        public ValueTask<TResponse> Handle(
            TMessage message,
            MessageHandle<TMessage, TResponse> next,
            CancellationToken cancellationToken
        )
        {
            log.Add($"logging:{typeof(TMessage).Name}");
            return next(message, cancellationToken);
        }
    }

    internal sealed class EchoOnlyMiddleware(TraceLog log) : IMiddleware<EchoRequest, string>
    {
        public ValueTask<string> Handle(
            EchoRequest message,
            MessageHandle<EchoRequest, string> next,
            CancellationToken cancellationToken
        )
        {
            log.Add("echo-only");
            return next(message, cancellationToken);
        }
    }

    internal sealed class StreamLoggingMiddleware(TraceLog log) : IStreamMiddleware<StreamRequest, int>
    {
        public async IAsyncEnumerable<int> Handle(
            StreamRequest message,
            StreamMessageHandle<StreamRequest, int> next,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            log.Add("stream:before");

            await foreach (var item in next(message, cancellationToken))
                yield return item;

            log.Add("stream:after");
        }
    }

    internal sealed class OpenStreamMiddleware<TRequest, TResponse>(TraceLog log) :
        IStreamMiddleware<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
    {
        public IAsyncEnumerable<TResponse> Handle(
            TRequest message,
            StreamMessageHandle<TRequest, TResponse> next,
            CancellationToken cancellationToken
        )
        {
            log.Add($"open-stream:{typeof(TRequest).Name}");
            return next(message, cancellationToken);
        }
    }

    internal sealed class EventLoggingMiddleware<TEvent>(TraceLog log) :
        IMiddleware<TEvent> where TEvent : IMessage
    {
        public ValueTask Handle(
            TEvent message,
            MessageHandle<TEvent> next,
            CancellationToken cancellationToken
        )
        {
            log.Add($"event-mw:{typeof(TEvent).Name}");
            return next(message, cancellationToken);
        }
    }
}

public sealed record VarianceQuery : IRequest<List<string>>;

public sealed class VarianceQueryHandler : IRequestHandler<VarianceQuery, List<string>>
{
    public ValueTask<List<string>> Handle(VarianceQuery message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new List<string>());
}

// List<string> meets IEnumerable<object> only through IEnumerable<out T>.
public sealed class VarianceAudit<TMessage, TResponse>(TraceLog log) : IMiddleware<TMessage, TResponse>
    where TMessage : IMessage<TResponse>
    where TResponse : IEnumerable<object>
{
    public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        log.Add("audited");
        return next(message, cancellationToken);
    }
}