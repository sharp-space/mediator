using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Fixtures;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Registration;

public sealed class MediatorServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMediator_ReturnsServiceCollection_ForFluentChaining()
    {
        // Regression: AddMediator returns the same IServiceCollection, so calls chain fluently.
        var services = new ServiceCollection();
        var returned = services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        Assert.Same(services, returned);
    }

    [Fact]
    public void DefaultLifetime_IsScoped()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        var descriptor = services.Single(x => x.ServiceType == typeof(IMediator));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void WithLifetime_Singleton_PropagatesToMediatorRegistration()
    {
        // Regression: the lifetime set through WithLifetime must reach the IMediator registration.
        var services = new ServiceCollection();
        services.AddMediator(x => x.WithLifetime(ServiceLifetime.Singleton));
        var descriptor = services.Single(x => x.ServiceType == typeof(IMediator));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public void RequesterAndPublisher_ForwardToTheOneMediator(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.WithLifetime(lifetime));

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        Assert.Same(mediator, scope.ServiceProvider.GetRequiredService<IRequester>());
        Assert.Same(mediator, scope.ServiceProvider.GetRequiredService<IPublisher>());
    }

    [Fact]
    public async Task RequesterAndPublisher_DispatchLikeTheMediator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TraceLog>();
        services.AddMediator(x => x
            .AddHandler<EchoRequestHandler>()
            .AddHandler<FirstEventHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var requester = scope.ServiceProvider.GetRequiredService<IRequester>();
        Assert.Equal("echo:a", await requester.Request<EchoRequest, string>(new EchoRequest("a")));

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new TestEvent("b"));
        Assert.Contains("first:b", scope.ServiceProvider.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    public void RequesterAndPublisher_FollowAReplacedMediator()
    {
        var replacement = new ReplacementMediator();
        var services = new ServiceCollection();
        services.AddSingleton<IMediator>(replacement);
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        Assert.Same(replacement, scope.ServiceProvider.GetRequiredService<IRequester>());
        Assert.Same(replacement, scope.ServiceProvider.GetRequiredService<IPublisher>());
    }

    [Fact]
    public void RepeatedAddMediator_RegistersRequesterAndPublisherOnce()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        services.AddMediator(x => x.AddHandler<FirstEventHandler>());
        Assert.Single(services, x => x.ServiceType == typeof(IRequester));
        Assert.Single(services, x => x.ServiceType == typeof(IPublisher));
    }

    [Fact]
    public async Task WithLifetime_Transient_GivesFreshHandlerInstancePerCall()
    {
        // Regression: caching reuses the handler captured in the cached chain, so it must be bypassed for Transient.
        // AddMediator sets cached = (lifetime != Transient), so Transient resolves a fresh handler per call and the
        // per-instance cache array is never allocated/used.
        var services = new ServiceCollection();
        services.AddSingleton<InstanceTracker>();
        services.AddMediator(x => x
            .WithLifetime(ServiceLifetime.Transient)
            .AddHandler<TrackingHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Request<TrackingRequest, int>(new TrackingRequest());
        await mediator.Request<TrackingRequest, int>(new TrackingRequest());
        await mediator.Request<TrackingRequest, int>(new TrackingRequest());

        var tracker = scope.ServiceProvider.GetRequiredService<InstanceTracker>();
        Assert.Equal(3, tracker.Instances.Count);
    }

    [Fact]
    public void WithLifetime_AfterAddHandler_StillAppliesToHandlerRegistrations()
    {
        // Handler registrations are deferred and applied with the builder's final lifetime, so the WithLifetime/Add*
        // call order does not matter (previously call-order-sensitive: the handler would silently keep the default
        // Scoped lifetime while processors became Singleton).
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>().WithLifetime(ServiceLifetime.Singleton));
        var descriptor = services.First(x => x.ImplementationType == typeof(EchoRequestHandler));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void WithEventPublisher_ReplacesDefault()
    {
        // Regression: WithEventPublisher replaces the default IEventPublisher rather than adding another.
        var services = new ServiceCollection();
        services.AddMediator(x => x.WithEventPublisher<SequentialPublisher>());
        var publisherRegistrations = services
            .Where(x => x.ServiceType == typeof(IEventPublisher)).ToArray();

        var item = Assert.Single(publisherRegistrations);
        Assert.Equal(typeof(SequentialPublisher), item.ImplementationType);
    }

    [Fact]
    public void RepeatedAddMediator_InheritsFirstGlobalConfiguration()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .WithLifetime(ServiceLifetime.Singleton)
            .WithEventPublisher<SequentialPublisher>());

        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        Assert.Equal(
            ServiceLifetime.Singleton,
            services.Single(x => x.ImplementationType == typeof(EchoRequestHandler)).Lifetime);

        Assert.Equal(
            typeof(SequentialPublisher),
            services.Single(x => x.ServiceType == typeof(IEventPublisher)).ImplementationType);
    }

    [Fact]
    public void RepeatedAddMediator_AllowsSameExplicitGlobalConfiguration()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .WithLifetime(ServiceLifetime.Singleton)
            .WithEventPublisher<SequentialPublisher>());

        services.AddMediator(x => x
            .WithLifetime(ServiceLifetime.Singleton)
            .WithEventPublisher<SequentialPublisher>());
    }

    [Fact]
    public void RepeatedAddMediator_RejectsConflictingLifetime()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.WithLifetime(ServiceLifetime.Singleton));
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.WithLifetime(ServiceLifetime.Scoped)));

        Assert.Contains("already fixed", exception.Message);
        Assert.Contains(nameof(ServiceLifetime.Singleton), exception.Message);
        Assert.Contains(nameof(ServiceLifetime.Scoped), exception.Message);
    }

    [Fact]
    public void RepeatedAddMediator_RejectsConflictingPublisher()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.WithEventPublisher<SequentialPublisher>());
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.WithEventPublisher<ParallelPublisher>()));

        Assert.Contains("already fixed", exception.Message);
        Assert.Contains(nameof(SequentialPublisher), exception.Message);
        Assert.Contains(nameof(ParallelPublisher), exception.Message);
    }

    [Fact]
    public void DirectPublisherRegistrationBeforeAddMediator_KeepsPrecedence()
    {
        var publisher = new SequentialPublisher();
        var services = new ServiceCollection();
        services.AddSingleton<IEventPublisher>(publisher);
        services.AddMediator(x => x.WithEventPublisher<ParallelPublisher>());
        Assert.Same(publisher,
            services.Single(x => x.ServiceType == typeof(IEventPublisher)).ImplementationInstance);
    }

    [Fact]
    public void KeyedHandlerServices_AreNotMistakenForAConflict()
    {
        // The mediator never resolves keyed services, so they cannot conflict with its handler.
        var services = new ServiceCollection();
        services.AddKeyedScoped<IRequestHandler<EchoRequest, string>, EchoRequestHandler>("first");
        services.AddKeyedScoped<IRequestHandler<EchoRequest, string>>("second", static (_, _) => new EchoRequestHandler());
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        Assert.Equal(3, services.Count(static x => x.ServiceType == typeof(IRequestHandler<EchoRequest, string>)));
    }

    [Fact]
    public void RepeatedHandlerRegistration_DedupesSameImplementation()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        Assert.Single(services, x =>
            x.ServiceType == typeof(IRequestHandler<EchoRequest, string>) &&
            x.ImplementationType == typeof(EchoRequestHandler));
    }

    [Fact]
    // AddMediator checks its registrations against one set of the collection. These pin that set to what
    // TryAddEnumerable compares: the implementation a descriptor declares, or the type of its instance.
    public void HandlerRegisteredDirectly_IsNotRegisteredAgain()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestHandler<EchoRequest, string>, EchoRequestHandler>();
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        Assert.Single(services, static x => x.ServiceType == typeof(IRequestHandler<EchoRequest, string>));
    }

    [Fact]
    // A Singleton mediator resolves from the root provider, where a scoped handler, middleware or publisher would
    // live as long as the process. AddMediator rejects one already registered, and the first mediator one
    // registered after it.
    public void SingletonMediator_RejectsScopedServices()
    {
        var before = new ServiceCollection();
        before.AddScoped<IRequestHandler<EchoRequest, string>, EchoRequestHandler>();
        var early = Assert.Throws<InvalidOperationException>(() =>
            before.AddMediator(static x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<EchoRequestHandler>()));

        var after = new ServiceCollection();
        after.AddMediator(static x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<EchoRequestHandler>());
        after.AddScoped<IEventPublisher, SequentialPublisher>();

        using var provider = after.BuildServiceProvider();
        var late = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IMediator>);
        Assert.Contains($"'{typeof(IRequestHandler<EchoRequest, string>)}'", early.Message);
        Assert.Contains($"'{typeof(IEventPublisher)}'", late.Message);
    }

    [Fact]
    // A Singleton mediator resolves a Transient handler, middleware or publisher from the root provider for each
    // dispatch, and the root provider keeps every disposable instance it creates until it is disposed, so a Singleton
    // mediator dispatching to a disposable Transient handler grew the process with every dispatch. AddMediator rejects
    // one already registered, whether IDisposable or only IAsyncDisposable, and the first mediator one registered after
    // it, judged by the type its factory is declared to return.
    public void SingletonMediator_RejectsDisposableTransientServices()
    {
        var before = new ServiceCollection();
        before.AddSingleton<Counter>();
        before.AddTransient<IRequestHandler<DisposableRequest, int>, DisposableRequestHandler>();
        before.AddTransient(typeof(IMiddleware<,>), typeof(AsyncDisposableMiddleware<,>));
        var early = Assert.Throws<InvalidOperationException>(() =>
            before.AddMediator(static x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<EchoRequestHandler>()));

        var after = new ServiceCollection();
        after.AddMediator(static x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<EchoRequestHandler>());
        after.AddTransient<IEventPublisher, DisposablePublisher>(static _ => new DisposablePublisher());
        using var provider = after.BuildServiceProvider();
        var late = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IMediator>);
        Assert.Contains($"'{typeof(DisposableRequestHandler)}'", early.Message);
        Assert.Contains($"'{typeof(AsyncDisposableMiddleware<,>)}'", early.Message);
        Assert.Contains($"'{typeof(DisposablePublisher)}'", late.Message);
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    // A Scoped or Transient mediator resolves from the scope it was resolved in, which disposes what it created when it
    // ends, so the rejection above stays with the Singleton mediator.
    public async Task ScopedOrTransientMediator_AcceptsDisposableTransientServices(ServiceLifetime lifetime)
    {
        var disposed = new Counter();
        var services = new ServiceCollection();
        services.AddSingleton(disposed);
        services.AddTransient<IRequestHandler<DisposableRequest, int>, DisposableRequestHandler>();
        services.AddMediator(x => x.WithLifetime(lifetime));

        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            for (var i = 0; i < 3; i++)
                await mediator.Request<DisposableRequest, int>(new DisposableRequest());

            Assert.Equal(0, disposed.Value);
        }

        Assert.Equal(3, disposed.Value);
    }

    [Fact]
    public void HandlerRegisteredAsInstance_CountsAsItsImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestHandler<EchoRequest, string>>(new EchoRequestHandler());
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        Assert.Single(services, static x => x.ServiceType == typeof(IRequestHandler<EchoRequest, string>));
    }

    [Fact]
    public void HandlerAddedTwiceInOneCall_IsRegisteredOnce()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>().AddHandlers(typeof(EchoRequestHandler).Assembly));
        Assert.Single(services, static x =>
            x.ServiceType == typeof(IRequestHandler<EchoRequest, string>) &&
            x.ImplementationType == typeof(EchoRequestHandler));
    }

    [Fact]
    public void RepeatedAddMediator_KeepsDifferentEventHandlersAdditive()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<FirstEventHandler>());
        services.AddMediator(x => x.AddHandler<SecondEventHandler>());
        Assert.Equal(2, services.Count(x => x.ServiceType == typeof(IEventHandler<TestEvent>)));
    }

    [Fact]
    public void DifferentRequestHandlersForSameService_AreRejectedWithTypes()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.AddHandler<ConflictingEchoHandler>()));

        Assert.Contains(typeof(EchoRequestHandler).ToString(), exception.Message);
        Assert.Contains(typeof(ConflictingEchoHandler).ToString(), exception.Message);
    }

    [Fact]
    // A second handler registered after AddMediator conflicts as much as one registered by a later AddMediator call.
    // The first mediator reads the registrations as they then stand and rejects it, where the container used to hand
    // out whichever registration came last.
    public void RequestHandlerRegisteredAfterAddMediator_IsRejectedByTheFirstMediator()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<EchoRequestHandler>());
        services.AddScoped<IRequestHandler<EchoRequest, string>, ConflictingEchoHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var exception = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IMediator>());
        Assert.Contains(typeof(EchoRequestHandler).ToString(), exception.Message);
        Assert.Contains(typeof(ConflictingEchoHandler).ToString(), exception.Message);
    }

    [Fact]
    // AddMediator checks the registrations it returns with, so the first mediator of a provider built right after it
    // takes that answer. It used to read every registration again, which in an application of 1000 handlers took about
    // as many instructions as AddMediator's own checks.
    public void FirstMediator_WhenNothingIsRegisteredAfterAddMediator_DoesNotReadTheRegistrationsAgain()
    {
        var services = new CountingServiceCollection();
        services.AddMediator(static x => x.AddHandler<EchoRequestHandler>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        services.Reads = 0;
        scope.ServiceProvider.GetRequiredService<IMediator>();

        Assert.Equal(0, services.Reads);
    }

    [Fact]
    // A handler swapped in after AddMediator, as a test host's ConfigureTestServices does, leaves the number of
    // registrations as it was, so the first mediator has to see the swap by more than their count. A Transient handler
    // under a Scoped mediator is then resolved for each dispatch, where the mediator kept it from its eighth miss on.
    public async Task FirstMediator_SeesAHandlerSwappedInAfterAddMediator_UnderAScopedMediator()
    {
        var counter = new Counter();
        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddMediator(static x => x.WithLifetime(ServiceLifetime.Scoped).AddHandler<EchoRequestHandler>());
        services.RemoveAll<IRequestHandler<EchoRequest, string>>();
        services.AddTransient<IRequestHandler<EchoRequest, string>, CountedEchoHandler>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        for (var i = 0; i < 20; i++) await mediator.Request<EchoRequest, string>(new EchoRequest("x"));

        Assert.Equal(20, counter.Value);
    }

    [Fact]
    // The same swap to a Scoped handler under a Singleton mediator is rejected by the first mediator, which would
    // otherwise resolve the handler from the root provider and keep it for the life of the process.
    public void FirstMediator_RejectsAScopedHandlerSwappedInAfterAddMediator_UnderASingletonMediator()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Counter());
        services.AddMediator(static x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<EchoRequestHandler>());
        services.RemoveAll<IRequestHandler<EchoRequest, string>>();
        services.AddScoped<IRequestHandler<EchoRequest, string>, CountedEchoHandler>();
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IMediator>);
        Assert.Contains($"'{typeof(IRequestHandler<EchoRequest, string>)}'", exception.Message);
    }

    [Fact]
    // Every provider built from one collection checks the registrations it was built from. The check was the
    // collection's and, once a first provider had made it, read only how many registrations there were, so a provider
    // built after a swap that kept that number took the old answer: here the mediator kept the Transient handler from
    // its eighth miss on.
    public async Task SecondProvider_SeesAHandlerSwappedInAfterTheFirstProvider_UnderAScopedMediator()
    {
        var counter = new Counter();
        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddMediator(static x => x.WithLifetime(ServiceLifetime.Scoped).AddHandler<EchoRequestHandler>());

        await using (var first = services.BuildServiceProvider())
        using (var firstScope = first.CreateScope())
        {
            _ = firstScope.ServiceProvider.GetRequiredService<IMediator>();
        }

        services.RemoveAll<IRequestHandler<EchoRequest, string>>();
        services.AddTransient<IRequestHandler<EchoRequest, string>, CountedEchoHandler>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        for (var i = 0; i < 20; i++) await mediator.Request<EchoRequest, string>(new EchoRequest("x"));

        Assert.Equal(20, counter.Value);
    }

    [Fact]
    // The same late swap to a Scoped handler under a Singleton mediator is rejected by the second
    // provider's first mediator.
    public void SecondProvider_RejectsAScopedHandlerSwappedInAfterTheFirstProvider_UnderASingletonMediator()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Counter());
        services.AddMediator(static x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<EchoRequestHandler>());

        using (var first = services.BuildServiceProvider())
        {
            _ = first.GetRequiredService<IMediator>();
        }

        services.RemoveAll<IRequestHandler<EchoRequest, string>>();
        services.AddScoped<IRequestHandler<EchoRequest, string>, CountedEchoHandler>();
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IMediator>);
        Assert.Contains($"'{typeof(IRequestHandler<EchoRequest, string>)}'", exception.Message);
    }

    [Fact]
    // A provider built from registrations that still stand compares them with the ones last checked rather than reading
    // them again, however many providers came before it.
    public void SecondProvider_WhenNothingChanged_DoesNotReadTheRegistrationsAgain()
    {
        var services = new CountingServiceCollection();
        services.AddMediator(static x => x.AddHandler<EchoRequestHandler>());

        using (var first = services.BuildServiceProvider())
        using (var firstScope = first.CreateScope())
        {
            _ = firstScope.ServiceProvider.GetRequiredService<IMediator>();
        }

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        services.Reads = 0;
        scope.ServiceProvider.GetRequiredService<IMediator>();

        Assert.Equal(0, services.Reads);
    }

    [Fact]
    // The test runner compiles dynamic code, so there the container creates the mediator from its type and builds the
    // provider's ServiceRetention into that (see DefaultMediator.Descriptor).
    public void AddMediator_RegistersTheMediatorByItsType_WhereDynamicCodeIsCompiled()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        var descriptor = services.Single(x => x.ServiceType == typeof(IMediator));
        Assert.Equal(typeof(DefaultMediator), descriptor.ImplementationType);
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton, 1, true)]
    [InlineData(ServiceLifetime.Singleton, 1, false)]
    [InlineData(ServiceLifetime.Scoped, 8, true)]
    [InlineData(ServiceLifetime.Scoped, 8, false)]
    [InlineData(ServiceLifetime.Transient, 0, true)]
    [InlineData(ServiceLifetime.Transient, 0, false)]
    // The registration by type and the factory of a process without dynamic code, as under Native AOT, hand the
    // mediator the same answer from the provider's ServiceRetention. First, the miss from which a mediator of
    // its lifetime caches.
    public async Task EitherMediatorRegistration_CachesFromTheMissItsLifetimeWaitsFor(
        ServiceLifetime lifetime,
        int warmAfterMisses,
        bool dynamicCodeCompiled
    )
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.WithLifetime(lifetime).AddHandler<EchoRequestHandler>());
        services.Replace(DefaultMediator.Descriptor(lifetime, dynamicCodeCompiled));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var message = new EchoRequest("x");

        // Lookup finds a chain wherever the mediator keeps it: a Scoped one keeps the chains of high slots beside its
        // array, and EchoRequest's slot depends on the order in which the process first dispatched its messages.
        var slot = RequestSlot<EchoRequest, string>.Index;
        for (var miss = 1; miss < warmAfterMisses; miss++) await mediator.Request<EchoRequest, string>(message);

        Assert.Null(((DefaultMediator)mediator).Lookup(slot));

        await mediator.Request<EchoRequest, string>(message);
        Assert.Equal(warmAfterMisses != 0, ((DefaultMediator)mediator).Lookup(slot) is not null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // Then that a handler lives shorter than the mediator, so a Transient one under a Scoped mediator is resolved
    // for each dispatch.
    public async Task EitherMediatorRegistration_ResolvesAShorterLivedHandlerForEachDispatch(bool dynamicCodeCompiled)
    {
        var counter = new Counter();
        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddMediator(static x => x.WithLifetime(ServiceLifetime.Scoped).AddHandler<EchoRequestHandler>());
        services.RemoveAll<IRequestHandler<EchoRequest, string>>();
        services.AddTransient<IRequestHandler<EchoRequest, string>, CountedEchoHandler>();
        services.Replace(DefaultMediator.Descriptor(ServiceLifetime.Scoped, dynamicCodeCompiled));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        for (var i = 0; i < 20; i++) await mediator.Request<EchoRequest, string>(new EchoRequest("x"));

        Assert.Equal(20, counter.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // And that registrations it cannot honor fail the provider's first mediator.
    public void EitherMediatorRegistration_RejectsAScopedHandlerUnderASingletonMediator(bool dynamicCodeCompiled)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Counter());
        services.AddMediator(static x => x.WithLifetime(ServiceLifetime.Singleton).AddHandler<EchoRequestHandler>());
        services.RemoveAll<IRequestHandler<EchoRequest, string>>();
        services.AddScoped<IRequestHandler<EchoRequest, string>, CountedEchoHandler>();
        services.Replace(DefaultMediator.Descriptor(ServiceLifetime.Singleton, dynamicCodeCompiled));

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IMediator>);
        Assert.Contains($"'{typeof(IRequestHandler<EchoRequest, string>)}'", exception.Message);
    }

    [Fact]
    public void RequestHandlerConflict_DescribesImplementationInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestHandler<EchoRequest, string>>(new ConflictingEchoHandler());
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.AddHandler<EchoRequestHandler>()));

        Assert.Contains(typeof(ConflictingEchoHandler).ToString(), exception.Message);
    }

    [Fact]
    public void RequestHandlerConflict_DescribesImplementationFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestHandler<EchoRequest, string>>(_ => new ConflictingEchoHandler());
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.AddHandler<EchoRequestHandler>()));

        Assert.Contains(nameof(RequestHandlerConflict_DescribesImplementationFactory), exception.Message);
    }

    [Fact]
    public void DifferentStreamHandlersForSameService_AreRejectedWithTypes()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandler<StreamRequestHandler>());
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(x => x.AddHandler<ConflictingStreamHandler>()));

        Assert.Contains(typeof(StreamRequestHandler).ToString(), exception.Message);
        Assert.Contains(typeof(ConflictingStreamHandler).ToString(), exception.Message);
    }

    [Fact]
    public void AddHandlers_Assembly_SkipsInvalidTypes()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandlers(typeof(MediatorServiceCollectionExtensionsTests).Assembly));

        // Sanity: at least one of our test fakes registered.
        Assert.Contains(services, x => x.ImplementationType == typeof(VoidRequestHandler));
        Assert.Contains(services, x => x.ImplementationType == typeof(EchoRequestHandler));
    }

    [Fact]
    public async Task AddHandler_AcceptsHandlerImplementingMultipleEventInterfaces()
    {
        // Regression: a handler implementing several event interfaces receives each of those events.
        var log = new TraceLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddMediator(x => x.AddHandler<MultiEventHandler>());

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Publish(new TestEvent("t1"));
        await mediator.Publish(new OtherEvent("t2"));

        Assert.Contains("multi-TestEvent:t1", log.Entries);
        Assert.Contains("multi-OtherEvent:t2", log.Entries);
    }

    [Fact]
    public void AddMiddleware_OpenGeneric_RegistersClosedOverDiscoveredMessages()
    {
        // Open-generic middleware is closed over each applicable message at compile time, because open-generic DI
        // registration relies on runtime MakeGenericType and is not AOT-safe.
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddMiddleware(typeof(OpenGenericMiddleware<>)));
        Assert.Contains(services, x =>
            x.ServiceType == typeof(IMiddleware<VoidRequest>) &&
            x.ImplementationType == typeof(OpenGenericMiddleware<VoidRequest>));
    }

    [Fact]
    public void AddMiddleware_Closed_RegistersAsClosedGenericService()
    {
        // Regression: closed (non-generic) middleware registers as its closed generic service.
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddMiddleware<ConcreteRequestMiddleware>());
        var registered = services.Single(x => x.ImplementationType == typeof(ConcreteRequestMiddleware));
        Assert.Equal(typeof(IMiddleware<VoidRequest>), registered.ServiceType);
    }

    [Fact]
    public void AddMiddleware_OpenGenericStream_RegistersClosedOverDiscoveredStreams()
    {
        // Regression: open-generic stream middleware is closed over each discovered stream request at compile time
        // rather than registered as an open-generic service.
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddMiddleware(typeof(OpenGenericStreamMiddleware<,>)));
        Assert.Contains(services, x =>
            x.ServiceType == typeof(IStreamMiddleware<StreamRequest, int>) &&
            x.ImplementationType == typeof(OpenGenericStreamMiddleware<StreamRequest, int>));
    }

    [Fact]
    public void AddMiddleware_ClosedStream_RegistersAsClosedGenericService()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddMiddleware<ConcreteStreamMiddleware>());
        var registered = services.Single(x => x.ImplementationType == typeof(ConcreteStreamMiddleware));
        Assert.Equal(typeof(IStreamMiddleware<StreamRequest, int>), registered.ServiceType);
    }

    [Fact]
    // Stream middleware had a registration method of its own, which registered only a type's stream interfaces, so a
    // type that wrapped both a request and a stream took a call of each kind. One call now registers every middleware
    // interface a type implements.
    public void AddMiddleware_TypeWithRequestAndStreamInterfaces_RegistersBoth()
    {
        var services = new ServiceCollection();
        services.AddMediator(static x => x.AddMiddleware<RequestAndStreamMiddleware>());
        Assert.Contains(services, static x => x.ServiceType == typeof(IMiddleware<VoidRequest>));
        Assert.Contains(services, static x => x.ServiceType == typeof(IStreamMiddleware<StreamRequest, int>));
    }

    private sealed class ReplacementMediator : IMediator
    {
        public ValueTask Request<TRequest>(
            TRequest message,
            CancellationToken cancellationToken = default
        ) where TRequest : IRequest => throw new NotSupportedException();

        public ValueTask<TResponse> Request<TRequest, TResponse>(
            TRequest message,
            CancellationToken cancellationToken = default
        ) where TRequest : IRequest<TResponse> => throw new NotSupportedException();

        public ValueTask<TResponse> Request<TResponse>(
            IRequest<TResponse> message,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask<object?> Request(
            object message,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> RequestStream<TRequest, TResponse>(
            TRequest message,
            CancellationToken cancellationToken = default
        ) where TRequest : IStreamRequest<TResponse> => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> RequestStream<TResponse>(
            IStreamRequest<TResponse> message,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<object?> RequestStream(
            object message,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask Publish<TEvent>(
            TEvent message,
            CancellationToken cancellationToken = default
        ) where TEvent : IEvent => throw new NotSupportedException();

        public ValueTask Publish<TEvent>(
            TEvent message,
            IEventPublisher? publisher,
            CancellationToken cancellationToken = default
        ) where TEvent : IEvent => throw new NotSupportedException();

        public ValueTask Publish(
            object message,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    public sealed class InstanceTracker
    {
        private readonly HashSet<int> _instances = [];
        private readonly Lock _sync = new();

        public IReadOnlyCollection<int> Instances
        {
            get
            {
                lock (_sync)
                {
                    return [.. _instances];
                }
            }
        }

        public void Record(int id)
        {
            lock (_sync)
            {
                _instances.Add(id);
            }
        }
    }

    public sealed record TrackingRequest : IRequest<int>;

    public sealed class TrackingHandler(InstanceTracker tracker) : IRequestHandler<TrackingRequest, int>
    {
        public ValueTask<int> Handle(TrackingRequest message, CancellationToken cancellationToken)
        {
            var id = RuntimeHelpers.GetHashCode(this);
            tracker.Record(id);
            return ValueTask.FromResult(id);
        }
    }

    // Counts its instances, so a test sees how often the mediator resolved it.
    private sealed class CountedEchoHandler : IRequestHandler<EchoRequest, string>
    {
        public CountedEchoHandler(Counter counter) => counter.Increment();

        public ValueTask<string> Handle(EchoRequest message, CancellationToken cancellationToken) => ValueTask.FromResult(message.Message);
    }

    // A service collection that counts how often something enumerates it. It wraps a list rather than deriving from
    // one, since LINQ enumerates a List<T> through its own enumerator.
    private sealed class CountingServiceCollection : IServiceCollection
    {
        private readonly List<ServiceDescriptor> _descriptors = [];

        public int Reads { get; set; }

        public int Count => _descriptors.Count;

        public bool IsReadOnly => false;

        public ServiceDescriptor this[int index]
        {
            get => _descriptors[index];
            set => _descriptors[index] = value;
        }

        public IEnumerator<ServiceDescriptor> GetEnumerator()
        {
            Reads++;
            return _descriptors.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Add(ServiceDescriptor item) => _descriptors.Add(item);

        public void Clear() => _descriptors.Clear();

        public bool Contains(ServiceDescriptor item) => _descriptors.Contains(item);

        public void CopyTo(ServiceDescriptor[] array, int arrayIndex) => _descriptors.CopyTo(array, arrayIndex);

        public int IndexOf(ServiceDescriptor item) => _descriptors.IndexOf(item);

        public void Insert(int index, ServiceDescriptor item) => _descriptors.Insert(index, item);

        public bool Remove(ServiceDescriptor item) => _descriptors.Remove(item);

        public void RemoveAt(int index) => _descriptors.RemoveAt(index);
    }

    public abstract class AbstractRequestHandler : IRequestHandler<VoidRequest>
    {
        public abstract ValueTask Handle(VoidRequest message, CancellationToken cancellationToken);
    }

    private sealed class ConflictingEchoHandler : IRequestHandler<EchoRequest, string>
    {
        public ValueTask<string> Handle(EchoRequest message, CancellationToken cancellationToken) => ValueTask.FromResult("conflict");
    }

    private sealed class ConflictingStreamHandler : IStreamRequestHandler<StreamRequest, int>
    {
        public async IAsyncEnumerable<int> Handle(StreamRequest message, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return -1;
        }
    }

    public sealed record OtherEvent(string Tag) : IEvent;

    public sealed class MultiEventHandler(TraceLog log) : IEventHandler<TestEvent>, IEventHandler<OtherEvent>
    {
        public ValueTask Handle(OtherEvent message, CancellationToken cancellationToken)
        {
            log.Add($"multi-OtherEvent:{message.Tag}");
            return ValueTask.CompletedTask;
        }

        public ValueTask Handle(TestEvent message, CancellationToken cancellationToken)
        {
            log.Add($"multi-TestEvent:{message.Tag}");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class OpenGenericMiddleware<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
    {
        public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
            next(message, cancellationToken);
    }

    public sealed class ConcreteRequestMiddleware : IMiddleware<VoidRequest>
    {
        public ValueTask Handle(VoidRequest message, MessageHandle<VoidRequest> next, CancellationToken cancellationToken) =>
            next(message, cancellationToken);
    }

    public sealed class OpenGenericStreamMiddleware<TRequest, TResponse> : IStreamMiddleware<TRequest, TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        public IAsyncEnumerable<TResponse> Handle(
            TRequest message,
            StreamMessageHandle<TRequest, TResponse> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    public sealed class ConcreteStreamMiddleware : IStreamMiddleware<StreamRequest, int>
    {
        public IAsyncEnumerable<int> Handle(
            StreamRequest message,
            StreamMessageHandle<StreamRequest, int> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    public sealed class RequestAndStreamMiddleware : IMiddleware<VoidRequest>, IStreamMiddleware<StreamRequest, int>
    {
        public ValueTask Handle(VoidRequest message, MessageHandle<VoidRequest> next, CancellationToken cancellationToken) =>
            next(message, cancellationToken);

        public IAsyncEnumerable<int> Handle(
            StreamRequest message,
            StreamMessageHandle<StreamRequest, int> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    public sealed record DisposableRequest : IRequest<int>;

    public sealed class DisposableRequestHandler(Counter disposed) : IRequestHandler<DisposableRequest, int>, IDisposable
    {
        public void Dispose() => disposed.Increment();

        public ValueTask<int> Handle(DisposableRequest message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
    }

    public sealed class AsyncDisposableMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>, IAsyncDisposable
        where TMessage : IMessage<TResponse>
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TResponse> Handle(
            TMessage message,
            MessageHandle<TMessage, TResponse> next,
            CancellationToken cancellationToken
        ) => next(message, cancellationToken);
    }

    public sealed class DisposablePublisher : IEventPublisher, IDisposable
    {
        public void Dispose()
        {
        }

        public ValueTask Publish(
            IReadOnlyList<EventHandlerExecutor> executors,
            IEvent message,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }
}