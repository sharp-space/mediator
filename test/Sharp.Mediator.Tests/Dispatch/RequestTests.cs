using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator.Tests.Dispatch;

public sealed class RequestTests
{
    private static IMediator Create(Action<MediatorBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddMediator(configure);
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task TypedVoidRequest_RoutesToHandler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddMediator(x => x.AddHandler<VoidRequestHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var counter = scope.ServiceProvider.GetRequiredService<Counter>();

        await mediator.Request(new VoidRequest());

        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public async Task TypedRequest_WithResponse_ReturnsHandlerResult()
    {
        var mediator = Create(x => x.AddHandler<EchoRequestHandler>());
        var result = await mediator.Request<EchoRequest, string>(new EchoRequest("hello"));
        Assert.Equal("echo:hello", result);
    }

    [Fact]
    public async Task InterfaceTypedRequest_RoutesByMessageType()
    {
        var mediator = Create(x => x.AddHandler<EchoRequestHandler>());
        IRequest<string> message = new EchoRequest("interface");
        var result = await mediator.Request(message);
        Assert.Equal("echo:interface", result);
    }

    [Fact]
    public async Task ObjectRequest_RoutesByRuntimeType_ForResponseRequest()
    {
        var mediator = Create(x => x.AddHandler<EchoRequestHandler>());
        var result = await mediator.Request((object)new EchoRequest("object"));
        Assert.Equal("echo:object", result);
    }

    [Fact]
    public async Task ObjectRequest_VoidIRequest_RoutesCorrectly()
    {
        // Regression: the request processor cache previously could not match non-generic IRequest.
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddMediator(x => x.AddHandler<VoidRequestHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var counter = scope.ServiceProvider.GetRequiredService<Counter>();
        var result = await mediator.Request((object)new VoidRequest());
        Assert.Null(result);
        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public async Task Request_PropagatesHandlerException()
    {
        var mediator = Create(x => x.AddHandler<ThrowingRequestHandler>());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Request<ThrowingRequest, string>(new ThrowingRequest()).AsTask());

        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public async Task Request_HonorsCancellation()
    {
        var mediator = Create(x => x.AddHandler<EchoRequestHandler>());
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // EchoHandler doesn't observe the token but the API should still accept and pass it.
        var result = await mediator.Request<EchoRequest, string>(new EchoRequest("x"), cancellationTokenSource.Token);
        Assert.Equal("echo:x", result);
    }

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [Fact]
    public void ObjectRequest_ThrowsForUnknownType()
    {
        var mediator = Create(x => x.AddHandler<EchoRequestHandler>());
        Assert.Throws<ArgumentException>(() => { _ = mediator.Request("not-a-request"); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    // A request without a handler threw InvalidOperationException from the typed entry points and ArgumentException
    // from the others, one cause behind two types to catch. Every entry point throws InvalidOperationException for it
    // now; only an object that is not a message is an argument error.
    public void ObjectRequest_RequestWithoutAHandler_ThrowsInvalidOperationNamingIt(bool returnsNothing)
    {
        var mediator = Create(x => x.AddHandler<EchoRequestHandler>());
        object message = returnsNothing ? new RuntimeOrphanCommand() : new RuntimeOrphan();
        var exception = Assert.Throws<InvalidOperationException>(() => { _ = mediator.Request(message); });
        Assert.Contains(message.GetType().Name, exception.Message);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("event")]
    [InlineData("stream request")]
    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    // An object of another kind is an argument error, even one whose own handler is registered: Request(object) takes
    // any object, and these are told apart without reflection.
    public void ObjectRequest_ObjectOfAnotherKind_ThrowsArgumentException(string kind)
    {
        var mediator = Create(x => x.AddHandler<EchoRequestHandler>().AddHandler<FirstEventHandler>().AddHandler<StreamRequestHandler>());
        var message = kind switch
        {
            "object" => new object(),
            "event" => new TestEvent("x"),
            _ => new StreamRequest(1)
        };

        var exception = Assert.Throws<ArgumentException>(() => { _ = mediator.Request(message); });

        Assert.Contains(kind switch
        {
            "object" => "is not a message",
            "event" => "is an event with no request handler",
            _ => "has a stream handler and no request handler"
        }, exception.Message);

        Assert.Contains("it takes a request", exception.Message);
    }

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [Fact]
    public void InterfaceTypedDerivedRequest_WithDifferentResponseThanHandledBase_ThrowsDescriptiveUnknownHandler()
    {
        // Regression: the walk reached the base handler and only then noticed the response type, so the handler's side
        // effects happened, its ValueTask was dropped, and the caller was told no handler existed.
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddMediator(x => x.AddHandler<BaseResponseHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        IRequest<string> message = new DerivedDifferentResponseRequest();
        var exception = Assert.Throws<InvalidOperationException>(() => mediator.Request(message));
        Assert.Contains("No handler is registered", exception.Message);
        Assert.Contains("IRequest<System.String>", exception.Message);
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<Counter>().Value);
    }

    // ---- abstractions as the static type ----------------------------------------------------------------------------

    [Fact]
    public async Task VoidRequest_ThroughIRequestVariable_DispatchesByRuntimeType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddMediator(x => x.AddHandler<VoidRequestHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        IRequest message = new VoidRequest();
        await mediator.Request(message);

        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<Counter>().Value);
    }

    [Fact]
    public async Task ResponseRequest_ThroughInterfaceTypeArgument_DispatchesByRuntimeType()
    {
        var mediator = Create(x => x.AddHandler<EchoRequestHandler>());
        IRequest<string> message = new EchoRequest("a");
        Assert.Equal("echo:a", await mediator.Request<IRequest<string>, string>(message));
    }

    [Fact]
    public async Task ResponseRequest_ThroughBaseClassTypeArgument_UsesTheDerivedHandler()
    {
        var mediator = Create(x => x.AddHandler<ConcreteCommandHandler>());
        AbstractCommand message = new ConcreteCommand();
        Assert.Equal(7, await mediator.Request<AbstractCommand, int>(message));
    }

    public abstract class AbstractCommand : IRequest<int>;

    public sealed class ConcreteCommand : AbstractCommand;

    public sealed class ConcreteCommandHandler : IRequestHandler<ConcreteCommand, int>
    {
        public ValueTask<int> Handle(ConcreteCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(7);
    }

    public class BaseResponseRequest : IRequest<int>;

    private sealed class DerivedDifferentResponseRequest : BaseResponseRequest, IRequest<string>;

    public sealed class BaseResponseHandler(Counter counter) : IRequestHandler<BaseResponseRequest, int>
    {
        public ValueTask<int> Handle(BaseResponseRequest message, CancellationToken cancellationToken)
        {
            counter.Increment();
            return ValueTask.FromResult(1);
        }
    }

    // A type implementing both IRequest and IRequest<TResponse> is a build-time error (SMD002) rather than a runtime
    // ArgumentException — pinned in DiagnosticTests.BothRequestInterfaces_ReportsSmd002_WithLocation.
}