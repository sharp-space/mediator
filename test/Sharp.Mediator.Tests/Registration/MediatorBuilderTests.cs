using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;

// ReSharper disable UnusedTypeParameter — the invalid registration shape under test needs the type parameter.

namespace Sharp.Mediator.Tests.Registration;

// Covers the registration-builder surface: the Type-taking overloads, the assembly scanners, the wrong-interface /
// non-concrete validation throws, and the no-registrar guard.
public sealed class MediatorBuilderTests
{
    private static ServiceCollection Services() => new();

    // ---- null arguments ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("AddHandler", "handlerType")]
    [InlineData("AddHandlers", "assembly")]
    [InlineData("AddMiddleware", "middlewareType")]
    // A null type or assembly threw NullReferenceException from inside the builder, and a null service collection an
    // ArgumentNullException that named LINQ's parameter. Each entry point now names its own.
    public void RegistrationMethod_GivenNull_ThrowsArgumentNullExceptionNamingItsParameter(string method, string parameter)
    {
        Action<MediatorBuilder> register = method switch
        {
            "AddHandler" => static x => x.AddHandler(null!),
            "AddHandlers" => static x => x.AddHandlers(null!),
            _ => static x => x.AddMiddleware(null!)
        };

        var exception = Assert.Throws<ArgumentNullException>(() => Services().AddMediator(register));
        Assert.Equal(parameter, exception.ParamName);
    }

    [Fact]
    public void AddMediator_OnNullServices_ThrowsArgumentNullExceptionNamingServices()
    {
        var exception = Assert.Throws<ArgumentNullException>(static () => ((IServiceCollection)null!).AddMediator());
        Assert.Equal("services", exception.ParamName);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("value")]
    [InlineData("conflictMessage")]
    // A null key threw from inside the dictionary, and a null value or conflict message was stored or thrown as is.
    public void WithExclusiveOption_GivenNull_ThrowsArgumentNullExceptionNamingItsParameter(string parameter)
    {
        Action<MediatorBuilder> select = parameter switch
        {
            "key" => static x => x.WithExclusiveOption(null!, "on", "conflict"),
            "value" => static x => x.WithExclusiveOption("mode", null!, "conflict"),
            _ => static x => x.WithExclusiveOption("mode", "on", null!)
        };

        var exception = Assert.Throws<ArgumentNullException>(() => Services().AddMediator(select));
        Assert.Equal(parameter, exception.ParamName);
    }

    [Fact]
    // A package that extends the builder chains its own calls after this one, as it does after every
    // other builder method.
    public void WithExclusiveOption_ReturnsTheBuilder()
    {
        MediatorBuilder? builder = null;
        MediatorBuilder? returned = null;

        Services().AddMediator(x =>
        {
            builder = x;
            returned = x.WithExclusiveOption("mode", "on", "conflict");
        });

        Assert.NotNull(builder);
        Assert.Same(builder, returned);
    }

    // ---- generic/Type overloads -------------------------------------------------------------------------------------

    [Fact]
    public void AddHandler_Generic_RegistersHandler()
    {
        var services = Services();
        services.AddMediator(x => x.AddHandler<VoidRequestHandler>());
        Assert.Contains(services, d => d.ImplementationType == typeof(VoidRequestHandler));
    }

    [Theory]
    [InlineData(typeof(EchoRequestHandler))]
    [InlineData(typeof(FirstEventHandler))]
    [InlineData(typeof(StreamRequestHandler))]
    // One method registers every kind of handler; each kind had a method of its own that rejected the others.
    public void AddHandler_TypeOverload_RegistersEveryKind(Type handlerType)
    {
        var services = Services();
        services.AddMediator(x => x.AddHandler(handlerType));
        Assert.Contains(services, d => d.ImplementationType == handlerType);
    }

    [Fact]
    // A handler or middleware may implement other interfaces too, such as IDisposable, and is registered under its
    // handler or middleware interfaces only.
    public void AddHandler_TypeWithOtherInterfaces_RegistersOnlyItsHandlerInterfaces()
    {
        var services = Services();
        services.AddMediator(static x => x.AddHandler<MediatorServiceCollectionExtensionsTests.DisposableRequestHandler>());
        var registered = Assert.Single(services,
            static d => d.ImplementationType == typeof(MediatorServiceCollectionExtensionsTests.DisposableRequestHandler));

        Assert.Equal(typeof(IRequestHandler<MediatorServiceCollectionExtensionsTests.DisposableRequest, int>), registered.ServiceType);
    }

    [Fact]
    public void AddMiddleware_ClosedTypeWithOtherInterfaces_RegistersOnlyItsMiddlewareInterfaces()
    {
        var services = Services();
        services.AddMediator(static x => x.AddMiddleware<DisposableVoidRequestMiddleware>());
        var registered = Assert.Single(services, static d => d.ImplementationType == typeof(DisposableVoidRequestMiddleware));
        Assert.Equal(typeof(IMiddleware<VoidRequest>), registered.ServiceType);
    }

    [Fact]
    public void AddMiddleware_OpenGenericWithOtherInterfaces_ClosesOnlyItsMiddlewareInterfaces()
    {
        var services = Services();
        services.AddMediator(static x => x.AddMiddleware(typeof(MediatorServiceCollectionExtensionsTests.AsyncDisposableMiddleware<,>)));
        Assert.Contains(services, static d =>
            d.ServiceType == typeof(IMiddleware<EchoRequest, string>) &&
            d.ImplementationType == typeof(MediatorServiceCollectionExtensionsTests.AsyncDisposableMiddleware<EchoRequest, string>));

        Assert.DoesNotContain(services, static d => d.ServiceType == typeof(IAsyncDisposable));
    }

    // ---- invalid handler types --------------------------------------------------------------------------------------

    [Fact]
    [SuppressMessage("Usage", "CA2263",
        Justification = "Exercise Type-based registration validation rather than a different generic overload.")]
    // AddHandler accepts any class, so the requirement names every interface it registers under.
    public void AddHandler_PlainClassWithoutHandlerInterface_ThrowsWithRequirement()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddHandler(typeof(Counter))));

        Assert.Contains(
            "IRequestHandler<TRequest>, IRequestHandler<TRequest, TResponse>, IEventHandler<TEvent> or " +
            "IStreamRequestHandler<TRequest, TResponse>", exception.Message);
    }

    // ---- invalid middleware types -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(IMiddleware<VoidRequest>))]
    [InlineData(typeof(IStreamMiddleware<StreamRequest, int>))]
    public void AddMiddleware_Interface_Throws(Type middlewareType) =>
        Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(middlewareType)));

    [Theory]
    [InlineData(typeof(AbstractMiddleware))]
    [InlineData(typeof(AbstractStreamMiddleware))]
    public void AddMiddleware_Abstract_Throws(Type middlewareType) =>
        Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(middlewareType)));

    [Fact]
    // A struct was accepted, and the container then found no constructor for it at the first dispatch. AddHandler, and
    // the generic overload of AddMiddleware, already required a class.
    public void AddMiddleware_ValueType_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(typeof(StructMiddleware))));

        Assert.Contains("must be a concrete class", exception.Message);
    }

    [Fact]
    // One method registers every kind of middleware, so the requirement names every interface it accepts.
    public void AddMiddleware_OpenGenericWithoutMiddlewareInterface_ThrowsWithRequirement()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(typeof(OpenNonMiddleware<>))));

        Assert.Contains("IMiddleware<TMessage>, IMiddleware<TMessage, TResponse> or IStreamMiddleware<TRequest, TResponse>",
            exception.Message);
    }

    [Fact]
    public void AddMiddleware_PartiallyClosedGenericType_Throws()
    {
        var messageParameter = typeof(MessageParameterCarrier<>).GetGenericArguments()[0];
        var partiallyClosed = typeof(PartiallyClosedMiddleware<,>)
            .MakeGenericType(messageParameter, typeof(int));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(partiallyClosed)));

        Assert.Contains("closed type or an open-generic type definition", exception.Message);
    }

    [Fact]
    [SuppressMessage("Usage", "CA2263",
        Justification = "Exercise Type-based registration validation rather than a different generic overload.")]
    public void AddMiddleware_ClosedTypeWithoutMiddlewareInterface_ThrowsWithRequirement()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(typeof(Counter))));

        Assert.Contains("IMiddleware<TMessage>, IMiddleware<TMessage, TResponse> or IStreamMiddleware<TRequest, TResponse>",
            exception.Message);
    }

    [Fact]
    public void AddMiddleware_OpenGenericWithFixedArgument_ThrowsMirrorGuidance()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(typeof(FixedResponseMiddleware<>))));

        Assert.Contains("one-for-one in the same order", exception.Message);
    }

    [Fact]
    public void AddMiddleware_OpenGenericWithReorderedArguments_ThrowsMirrorGuidance()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(typeof(ReorderedMiddleware<,>))));

        Assert.Contains("one-for-one in the same order", exception.Message);
    }

    [Fact]
    public void AddMiddleware_OpenGenericWithOnlySecondArgumentFixed_ThrowsMirrorGuidance()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddMediator(x => x.AddMiddleware(typeof(SecondFixedResponseMiddleware<,>))));

        Assert.Contains("one-for-one in the same order", exception.Message);
    }

    [Fact]
    public void AddMiddleware_IgnoresUnrelatedGenericServiceDescriptors()
    {
        var services = Services();
        ((IServiceCollection)services).Add(
            ServiceDescriptor.Singleton(typeof(UnrelatedGenericService<>), typeof(UnrelatedGenericService<>)));

        services.AddMediator(x => x.AddMiddleware(typeof(OpenVoidMiddleware<>)));
        Assert.Single(services, descriptor =>
            descriptor.ServiceType.IsGenericType &&
            descriptor.ServiceType.GetGenericTypeDefinition() == typeof(UnrelatedGenericService<>) &&
            descriptor.ImplementationType == typeof(UnrelatedGenericService<>));
    }

    // ---- assembly scanners ------------------------------------------------------------------------------------------

    [Fact]
    // The assembly also declares handlers that cannot be registered, such as an abstract one, which AddHandlers skips.
    public void AddHandlers_AssemblyWithInvalidTypes_RegistersOnlyItsValidHandlers()
    {
        var services = Services();
        services.AddMediator(x => x.AddHandlers(typeof(MediatorBuilderTests).Assembly));
        Assert.Contains(services, d => d.ImplementationType == typeof(EchoRequestHandler));
        Assert.Contains(services, d => d.ImplementationType == typeof(FirstEventHandler));
        Assert.Contains(services, d => d.ImplementationType == typeof(StreamRequestHandler));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(MediatorServiceCollectionExtensionsTests.AbstractRequestHandler));
    }

    // ---- generated assembly list (global state; assembly parallelization disabled) ----------------------------------

    [Fact]
    public void AddMediator_WithoutRegistrars_Throws()
    {
        var field = typeof(MediatorRuntime).GetField("HandlerServiceFactories", BindingFlags.NonPublic | BindingFlags.Static)!;
        var list = (IList)field.GetValue(null)!;
        var saved = new object[list.Count];
        list.CopyTo(saved, 0);

        try
        {
            list.Clear();

            var ex = Assert.Throws<InvalidOperationException>(() => Services().AddMediator());
            Assert.Contains("No generated mediator registrar", ex.Message);
        }
        finally
        {
            foreach (var r in saved) list.Add(r);
        }
    }

    [Fact]
    public void HandlerServices_AreRead_OnlyForAHandlerRegisteredByType()
    {
        var field = typeof(MediatorRuntime).GetField("HandlerServiceFactories", BindingFlags.NonPublic | BindingFlags.Static)!;
        var list = (List<Func<Type[]>>)field.GetValue(null)!;
        var reads = 0;
        Func<Type[]> counting = () =>
        {
            reads++;
            return [];
        };

        list.Add(counting);

        try
        {
            Services().AddMediator(x => x.AddHandlers(typeof(MediatorBuilderTests).Assembly));

            Assert.Equal(0, reads);

            Services().AddMediator(x => x.AddHandler<VoidRequestHandler>());

            Assert.Equal(1, reads);
        }
        finally
        {
            list.Remove(counting);
        }
    }

    // ---- handler types the builder rejects --------------------------------------------------------------------------

    [SuppressMessage("Usage", "CA2263",
        Justification = "Exercise Type-based registration validation rather than a different generic overload.")]
    [Fact]
    public void AddHandler_AbstractClass_Throws()
    {
        // Regression: AddHandler rejects an abstract class, which could never be constructed.
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.AddHandler(typeof(MediatorServiceCollectionExtensionsTests.AbstractRequestHandler))));
    }

    [Fact]
    [SuppressMessage("Usage", "CA2263",
        Justification = "Exercise Type-based registration validation rather than a different generic overload.")]
    public void AddHandler_InterfaceType_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.AddHandler(typeof(IRequestHandler<VoidRequest>))));
    }

    [Fact]
    public void AddHandler_OpenGeneric_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.AddHandler(typeof(GenericHandler<>))));
    }

    [Fact]
    [SuppressMessage("Usage", "CA2263",
        Justification = "Exercise Type-based registration validation rather than a different generic overload.")]
    public void AddHandler_ClosedGenericHandler_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.AddHandler(typeof(GenericHandler<VoidRequest>))));
    }

    [Fact]
    public void CloseGapsViaReflection_InconsistentServicePair_Throws()
    {
        // The public pipeline constructs matching pairs. Test its defensive invariant directly with a corrupt pair.
        var services = new ServiceCollection();
        var registration = new MediatorRegistration(services, ServiceLifetime.Transient);
        var pairs = new List<(Type, Type[])> { (typeof(IMiddleware<>), [typeof(Ping)]) };
        var error = Assert.Throws<InvalidOperationException>(() =>
            MediatorBuilder.CloseGapsViaReflection(registration, typeof(List<>), pairs));

        Assert.Contains("Middleware closure invariant failed", error.Message);
        Assert.Empty(services);
    }

    private abstract class AbstractMiddleware : IMiddleware<VoidRequest>
    {
        public abstract ValueTask Handle(VoidRequest message, MessageHandle<VoidRequest> next, CancellationToken cancellationToken);
    }

    private abstract class AbstractStreamMiddleware : IStreamMiddleware<StreamRequest, int>
    {
        public abstract IAsyncEnumerable<int> Handle(
            StreamRequest message,
            StreamMessageHandle<StreamRequest, int> next,
            CancellationToken cancellationToken
        );
    }

    private sealed class OpenNonMiddleware<T>;

    public sealed class OpenVoidMiddleware<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
    {
        public ValueTask Handle(
            TMessage message,
            MessageHandle<TMessage> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    private sealed class UnrelatedGenericService<T>;

    public sealed class OpenResponseMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
    {
        public ValueTask<TResponse> Handle(
            TMessage message,
            MessageHandle<TMessage, TResponse> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    [SuppressMessage(
        "Sharp.Mediator", "SMD011", Justification = "Deliberately invalid runtime-registration fixture.")]
    private sealed class FixedResponseMiddleware<TMessage> : IMiddleware<TMessage, string> where TMessage : IMessage<string>
    {
        public ValueTask<string> Handle(
            TMessage message,
            MessageHandle<TMessage, string> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    [SuppressMessage(
        "Sharp.Mediator", "SMD011", Justification = "Deliberately invalid runtime-registration fixture.")]
    private sealed class ReorderedMiddleware<TResponse, TMessage> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
    {
        public ValueTask<TResponse> Handle(
            TMessage message,
            MessageHandle<TMessage, TResponse> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    [SuppressMessage("Sharp.Mediator", "SMD011", Justification = "Deliberately invalid runtime-registration fixture.")]
    private sealed class SecondFixedResponseMiddleware<TMessage, TUnused> : IMiddleware<TMessage, string> where TMessage : IMessage<string>
    {
        public ValueTask<string> Handle(
            TMessage message,
            MessageHandle<TMessage, string> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    private sealed class MessageParameterCarrier<TMessage> where TMessage : IMessage;

    [SuppressMessage("Sharp.Mediator", "SMD011", Justification = "Deliberately invalid runtime-registration fixture.")]
    private sealed class PartiallyClosedMiddleware<TMessage, TUnused> : IMiddleware<TMessage> where TMessage : IMessage
    {
        public ValueTask Handle(
            TMessage message,
            MessageHandle<TMessage> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    public struct StructMiddleware : IMiddleware<VoidRequest>
    {
        public ValueTask Handle(VoidRequest message, MessageHandle<VoidRequest> next, CancellationToken cancellationToken) =>
            next(message, cancellationToken);
    }

    public sealed class DisposableVoidRequestMiddleware : IMiddleware<VoidRequest>, IDisposable
    {
        public void Dispose()
        {
        }

        public ValueTask Handle(VoidRequest message, MessageHandle<VoidRequest> next, CancellationToken cancellationToken) =>
            next(message, cancellationToken);
    }

    private class GenericHandler<TMessage> : IRequestHandler<EchoRequest, string> where TMessage : class
    {
        public ValueTask<string> Handle(EchoRequest message, CancellationToken cancellationToken) =>
            ValueTask.FromResult("generic");
    }

    public sealed record Ping : IRequest;
}