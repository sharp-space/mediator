using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator.Tests.Dispatch;

// The mediator resolves handlers and middleware itself. An instance that will not keep the chain skips building one
// when no middleware applies, so each message kind has two uncached shapes, and DI is only promised to return an
// IEnumerable, so each kind has to accept one that is not an array.
public sealed class HandlerResolutionTests
{
    private static ServiceProvider Provider(Action<MediatorBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ResolvedTrail>();
        services.AddMediator(configure);
        return services.BuildServiceProvider();
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        List<T> items = [];
        await foreach (var item in stream) items.Add(item);

        return items;
    }

    // ---- uncached, with middleware ----------------------------------------------------------------------------------

    [Fact]
    public async Task UncachedVoidRequest_RunsItsMiddleware_OnEveryDispatch()
    {
        await using var provider = Provider(static x => x
            .AddHandler<ResolvedVoidHandler>().AddMiddleware<ResolvedVoidMiddleware>());

        var mediator = new DefaultMediator(provider);
        await mediator.Request(new ResolvedVoid());
        await mediator.Request(new ResolvedVoid());

        Assert.Equal(["middleware", "handler", "middleware", "handler"], provider.GetRequiredService<ResolvedTrail>().Steps);
    }

    [Fact]
    public async Task UncachedResponseRequest_RunsItsMiddleware_OnEveryDispatch()
    {
        await using var provider = Provider(static x => x
            .AddHandler<ResolvedResponseHandler>().AddMiddleware<ResolvedResponseMiddleware>());

        var mediator = new DefaultMediator(provider);
        Assert.Equal(3, await mediator.Request<ResolvedResponse, int>(new ResolvedResponse(1)));
        Assert.Equal(4, await mediator.Request<ResolvedResponse, int>(new ResolvedResponse(2)));
    }

    [Fact]
    public async Task UncachedStream_RunsItsMiddleware_OnEveryDispatch()
    {
        await using var provider = Provider(static x => x
            .AddHandler<ResolvedStreamHandler>().AddMiddleware<ResolvedStreamMiddleware>());

        var mediator = new DefaultMediator(provider);
        Assert.Equal([1, 2, 3], await Collect(mediator.RequestStream<ResolvedStream, int>(new ResolvedStream())));
        Assert.Equal([1, 2, 3], await Collect(mediator.RequestStream<ResolvedStream, int>(new ResolvedStream())));
    }

    [Fact]
    public async Task UncachedEvent_RunsItsMiddleware_ForEachHandler()
    {
        await using var provider = Provider(static x => x
            .AddHandler<ResolvedEventHandler>().AddHandler<ResolvedLaterEventHandler>().AddMiddleware<ResolvedEventMiddleware>());

        var mediator = new DefaultMediator(provider);
        await mediator.Publish(new ResolvedEvent());

        Assert.Equal(["middleware", "handler", "middleware", "later"], provider.GetRequiredService<ResolvedTrail>().Steps);
    }

    // ---- uncached, without middleware -------------------------------------------------------------------------------

    [Fact]
    public async Task UncachedStream_CallsItsHandler_WithoutMiddleware()
    {
        await using var provider = Provider(static x => x.AddHandler<ResolvedStreamHandler>());
        var mediator = new DefaultMediator(provider);
        Assert.Equal([1, 2], await Collect(mediator.RequestStream<ResolvedStream, int>(new ResolvedStream())));
    }

    // ---- a kind of middleware nothing registers ---------------------------------------------------------------------

    [Theory]
    [InlineData(null, new Type[0])]
    [InlineData(
        typeof(ResolvedVoidMiddleware),
        new[] { typeof(IMiddleware<ResolvedVoid>), typeof(IMiddleware<ResolvedEvent>) })]
    [InlineData(typeof(ResolvedResponseMiddleware), new[] { typeof(IMiddleware<ResolvedResponse, int>) })]
    [InlineData(typeof(ResolvedStreamMiddleware), new[] { typeof(IStreamMiddleware<ResolvedStream, int>) })]
    // An instance that has not kept a message's chain looks the message's middleware up for each dispatch, but only of
    // a kind the provider registers: void requests and events share IMiddleware<TMessage>, and requests with a response
    // and streams have a kind each.
    public async Task UncachedDispatch_LooksUpOnlyTheKindsOfMiddlewareRegistered(Type? middleware, Type[] lookedUp)
    {
        await using var provider = Provider(x =>
        {
            x.AddHandler<ResolvedVoidHandler>().AddHandler<ResolvedResponseHandler>()
                .AddHandler<ResolvedEventHandler>().AddHandler<ResolvedStreamHandler>();

            if (middleware is not null) x.AddMiddleware(middleware);
        });

        List<Type> asked = [];
        var retention = provider.GetRequiredService<ServiceRetention>();
        var mediator = new DefaultMediator(new AskingProvider(provider, asked), retention);

        await mediator.Request(new ResolvedVoid());
        await mediator.Request<ResolvedResponse, int>(new ResolvedResponse(1));
        await mediator.Publish(new ResolvedEvent());
        await Collect(mediator.RequestStream<ResolvedStream, int>(new ResolvedStream()));

        Assert.Equal(lookedUp, asked.Where(IsMiddlewares).Select(static x => x.GetGenericArguments()[0]));
    }

    [Fact]
    // Another container may resolve middleware registered through its own API rather than the service collection, so a
    // mediator over one looks up every kind. Here that container resolves middleware of the void request, which the
    // collection does not register, and the request runs it.
    public async Task UncachedDispatch_UnderAnotherContainer_LooksUpEveryKindOfMiddleware()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ResolvedTrail>();
        services.AddMediator(static x => x
            .AddHandler<ResolvedVoidHandler>().AddHandler<ResolvedResponseHandler>()
            .AddHandler<ResolvedEventHandler>().AddHandler<ResolvedStreamHandler>());

        await using var provider = services.BuildServiceProvider();
        var trail = provider.GetRequiredService<ResolvedTrail>();
        List<Type> asked = [];
        var container = new OtherContainer(provider, asked, [new ResolvedVoidMiddleware(trail)]);
        var factory = services.Single(static x => x.ServiceType == typeof(ServiceRetention)).ImplementationFactory!;
        var mediator = new DefaultMediator(container, (ServiceRetention)factory(container));

        await mediator.Request(new ResolvedVoid());
        await mediator.Request<ResolvedResponse, int>(new ResolvedResponse(1));
        await mediator.Publish(new ResolvedEvent());
        await Collect(mediator.RequestStream<ResolvedStream, int>(new ResolvedStream()));

        Type[] everyKind =
        [
            typeof(IMiddleware<ResolvedVoid>), typeof(IMiddleware<ResolvedResponse, int>),
            typeof(IMiddleware<ResolvedEvent>), typeof(IStreamMiddleware<ResolvedStream, int>)
        ];

        Assert.Equal(everyKind, asked.Where(IsMiddlewares).Select(static x => x.GetGenericArguments()[0]));
        Assert.Equal(["middleware", "handler", "handler"], trail.Steps);
    }

    private static bool IsMiddlewares(Type service) =>
        service.IsGenericType && service.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
        service.GetGenericArguments()[0] is { IsGenericType: true } element &&
        element.GetGenericTypeDefinition() is var definition &&
        (definition == typeof(IMiddleware<>) || definition == typeof(IMiddleware<,>) ||
            definition == typeof(IStreamMiddleware<,>));

    // ---- a container that returns something other than an array -----------------------------------------------------

    [Fact]
    public async Task VoidRequest_AcceptsAnyEnumerableOfMiddleware()
    {
        await using var provider = Provider(static x => x
            .AddHandler<ResolvedVoidHandler>().AddMiddleware<ResolvedVoidMiddleware>());

        var mediator = new DefaultMediator(new ListingProvider(provider), 1);
        await mediator.Request(new ResolvedVoid());

        Assert.Equal(["middleware", "handler"], provider.GetRequiredService<ResolvedTrail>().Steps);
    }

    [Fact]
    public async Task ResponseRequest_AcceptsAnyEnumerableOfMiddleware()
    {
        await using var provider = Provider(static x => x
            .AddHandler<ResolvedResponseHandler>().AddMiddleware<ResolvedResponseMiddleware>());

        var mediator = new DefaultMediator(new ListingProvider(provider), 1);
        Assert.Equal(3, await mediator.Request<ResolvedResponse, int>(new ResolvedResponse(1)));
    }

    [Fact]
    public async Task Stream_AcceptsAnyEnumerableOfMiddleware()
    {
        await using var provider = Provider(static x => x
            .AddHandler<ResolvedStreamHandler>().AddMiddleware<ResolvedStreamMiddleware>());

        var mediator = new DefaultMediator(new ListingProvider(provider), 1);
        Assert.Equal([1, 2, 3], await Collect(mediator.RequestStream<ResolvedStream, int>(new ResolvedStream())));
    }

    [Fact]
    public async Task Event_AcceptsAnyEnumerableOfHandlersAndMiddleware()
    {
        await using var provider = Provider(static x => x.AddHandler<ResolvedEventHandler>().AddMiddleware<ResolvedEventMiddleware>());
        var mediator = new DefaultMediator(new ListingProvider(provider), 1);
        await mediator.Publish(new ResolvedEvent());

        Assert.Equal(["middleware", "handler"], provider.GetRequiredService<ResolvedTrail>().Steps);
    }

    // ---- an override does not replace what the mediator keeps -------------------------------------------------------

    [Fact]
    public async Task PublishWithOverride_OnACachingMediator_LeavesTheDefaultPublisherInPlace()
    {
        var fallback = new RecordingPublisher();
        var services = new ServiceCollection();
        services.AddSingleton<ResolvedTrail>();
        services.AddSingleton<IEventPublisher>(fallback);
        services.AddMediator(static x => x.AddHandler<ResolvedEventHandler>());

        await using var provider = services.BuildServiceProvider();
        var mediator = new DefaultMediator(provider, 1);
        var replacement = new RecordingPublisher();

        await mediator.Publish(new ResolvedEvent(), replacement);
        await mediator.Publish(new ResolvedEvent());

        Assert.Equal(1, replacement.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    // A container other than Microsoft's, which resolves the middleware of the void request itself, as if registered
    // through its own API, and the rest through the service collection, recording the type of every service asked for.
    private sealed class OtherContainer(IServiceProvider inner, List<Type> asked, IMiddleware<ResolvedVoid>[] own) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            asked.Add(serviceType);
            return serviceType == typeof(IEnumerable<IMiddleware<ResolvedVoid>>) ? own : inner.GetService(serviceType);
        }
    }

    // Records the type of every service the mediator asks for.
    private sealed class AskingProvider(IServiceProvider inner, List<Type> asked) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            asked.Add(serviceType);
            return inner.GetService(serviceType);
        }
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public int Calls { get; private set; }

        public ValueTask Publish(IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken)
        {
            Calls++;
            return default;
        }
    }

    private sealed class ListingProvider(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) => inner.GetService(serviceType) switch
        {
            IMiddleware<ResolvedVoid>[] middlewares => middlewares.ToList(),
            IMiddleware<ResolvedResponse, int>[] middlewares => middlewares.ToList(),
            IStreamMiddleware<ResolvedStream, int>[] middlewares => middlewares.ToList(),
            IEventHandler<ResolvedEvent>[] handlers => handlers.ToList(),
            IMiddleware<ResolvedEvent>[] middlewares => middlewares.ToList(),
            var service => service
        };
    }
}

public sealed class ResolvedTrail
{
    public List<string> Steps { get; } = [];
}

public sealed record ResolvedEvent : IEvent;

public sealed class ResolvedEventHandler(ResolvedTrail trail) : IEventHandler<ResolvedEvent>
{
    public ValueTask Handle(ResolvedEvent message, CancellationToken cancellationToken)
    {
        trail.Steps.Add("handler");
        return default;
    }
}

public sealed class ResolvedLaterEventHandler(ResolvedTrail trail) : IEventHandler<ResolvedEvent>
{
    public ValueTask Handle(ResolvedEvent message, CancellationToken cancellationToken)
    {
        trail.Steps.Add("later");
        return default;
    }
}

public sealed class ResolvedEventMiddleware(ResolvedTrail trail) : IMiddleware<ResolvedEvent>
{
    public ValueTask Handle(ResolvedEvent message, MessageHandle<ResolvedEvent> next, CancellationToken cancellationToken)
    {
        trail.Steps.Add("middleware");
        return next(message, cancellationToken);
    }
}

public sealed record ResolvedVoid : IRequest;

public sealed class ResolvedVoidHandler(ResolvedTrail trail) : IRequestHandler<ResolvedVoid>
{
    public ValueTask Handle(ResolvedVoid message, CancellationToken cancellationToken)
    {
        trail.Steps.Add("handler");
        return default;
    }
}

public sealed class ResolvedVoidMiddleware(ResolvedTrail trail) : IMiddleware<ResolvedVoid>
{
    public ValueTask Handle(ResolvedVoid message, MessageHandle<ResolvedVoid> next, CancellationToken cancellationToken)
    {
        trail.Steps.Add("middleware");
        return next(message, cancellationToken);
    }
}

public sealed record ResolvedResponse(int Value) : IRequest<int>;

public sealed class ResolvedResponseHandler : IRequestHandler<ResolvedResponse, int>
{
    public ValueTask<int> Handle(ResolvedResponse message, CancellationToken cancellationToken) => new(message.Value + 1);
}

public sealed class ResolvedResponseMiddleware : IMiddleware<ResolvedResponse, int>
{
    public async ValueTask<int> Handle(ResolvedResponse message, MessageHandle<ResolvedResponse, int> next, CancellationToken cancellationToken) =>
        await next(message, cancellationToken) + 1;
}

public sealed record ResolvedStream : IStreamRequest<int>;

public sealed class ResolvedStreamHandler : IStreamRequestHandler<ResolvedStream, int>
{
    public async IAsyncEnumerable<int> Handle(ResolvedStream message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        yield return 2;
    }
}

public sealed class ResolvedStreamMiddleware : IStreamMiddleware<ResolvedStream, int>
{
    public async IAsyncEnumerable<int> Handle(
        ResolvedStream message,
        StreamMessageHandle<ResolvedStream, int> next,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (var item in next(message, cancellationToken))
            yield return item;

        yield return 3;
    }
}