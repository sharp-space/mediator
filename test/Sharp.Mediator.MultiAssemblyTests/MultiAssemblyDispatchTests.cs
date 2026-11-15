using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Fixtures;
using Sharp.Mediator.LibraryFixtures;
using Sharp.Mediator.MultiAssemblyTests.Fixtures;
using Sharp.Mediator.NoGeneratorFixtures;

namespace Sharp.Mediator.MultiAssemblyTests;

// End-to-end coverage for handlers generated into two assemblies (this one + Sharp.Mediator.LibraryFixtures).
// The previous architecture failed here in two silent ways: the first-registered dispatcher answered for the whole
// process (object/interface paths threw for the other assembly's messages), and open-generic middleware only closed
// over the registering compilation's messages.
public sealed class MultiAssemblyDispatchTests
{
    private static Type InternalCount { get; } =
        typeof(LibraryPing).Assembly.GetType("Sharp.Mediator.LibraryFixtures.InternalLibraryCount", true)!;

    private static ServiceProvider BuildProvider(Action<MediatorBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<LibraryTrace>();
        services.AddSingleton<TraceLog>();
        services.AddSingleton<PackagedTrace>();
        services.AddMediator(builder =>
        {
            builder
                .AddHandler<ApplicationPingHandler>()
                .AddHandler<DerivedCommandHandler>()
                .AddHandler<ApplicationLibraryEventHandler>()
                .AddHandler<LibraryPingHandler>()
                .AddHandler<LibraryVoidHandler>()
                .AddHandler<LibraryEventHandler>()
                .AddHandler<LibraryCountHandler>()
                .AddHandler<BaseCommandHandler>();

            configure?.Invoke(builder);
        });

        return services.BuildServiceProvider();
    }

    private static IMediator Mediator(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IMediator>();

    // ---- object/interface dispatch across assemblies ----------------------------------------------------------------

    [Fact]
    public async Task ObjectRequest_DispatchesMessagesFromBothAssemblies()
    {
        var mediator = Mediator(BuildProvider());
        Assert.Equal(7, await mediator.Request((object)new ApplicationPing(7)));
        Assert.Equal(70, await mediator.Request((object)new LibraryPing(7)));
        Assert.Null(await mediator.Request((object)new LibraryVoid()));
    }

    [Fact]
    public async Task InterfaceTypedRequest_DispatchesMessagesFromBothAssemblies()
    {
        var mediator = Mediator(BuildProvider());
        Assert.Equal(7, await mediator.Request(new ApplicationPing(7)));
        Assert.Equal(70, await mediator.Request(new LibraryPing(7)));
    }

    [Fact]
    public async Task ObjectPublish_DispatchesLibraryEvent_ToHandlersInBothAssemblies()
    {
        var provider = BuildProvider();
        var mediator = Mediator(provider);

        await mediator.Publish((object)new LibraryEvent("x"));

        var entries = provider.GetRequiredService<LibraryTrace>().Entries;
        Assert.Contains("library:x", entries);
        Assert.Contains("application:x", entries);
    }

    [Fact]
    public async Task ObjectRequestStream_DispatchesLibraryStream()
    {
        var mediator = Mediator(BuildProvider());
        var items = new List<object?>();
        await foreach (var item in mediator.RequestStream((object)new LibraryCount(3)))
            items.Add(item);

        Assert.Equal([0, 1, 2], items);
    }

    [Fact]
    public async Task InterfaceTypedRequestStream_DispatchesLibraryStream()
    {
        var mediator = Mediator(BuildProvider());
        var items = new List<int>();
        await foreach (var item in mediator.RequestStream(new LibraryCount(2)))
            items.Add(item);

        Assert.Equal([0, 1], items);
    }

    // ---- inheritance across assemblies (base-type walk) -------------------------------------------------------------

    [Fact]
    public async Task ObjectRequest_DerivedWithOwnHandler_RoutesToDerivedHandler()
    {
        // DerivedCommand lives here, BaseCommand in the library; the derived arm must win even though the two arms live
        // in different dispatchers.
        var mediator = Mediator(BuildProvider());
        Assert.Equal("derived:a", await mediator.Request((object)new DerivedCommand("a")));
        Assert.Equal("base:b", await mediator.Request((object)new BaseCommand("b")));
    }

    [Fact]
    public async Task ObjectRequest_UnhandledDerived_WalksToNearestAncestorHandler()
    {
        var mediator = Mediator(BuildProvider());
        Assert.Equal("derived:walk", await mediator.Request((object)new UnhandledDerivedCommand("walk")));
    }

    [Fact]
    public async Task InterfaceTypedRequest_UnhandledDerived_WalksToNearestAncestorHandler()
    {
        var mediator = Mediator(BuildProvider());
        Assert.Equal("derived:walk", await mediator.Request(new UnhandledDerivedCommand("walk")));
    }

    // ---- open-generic middleware union ------------------------------------------------------------------------------

    [Fact]
    public async Task ApplicationDeclaredOpenMiddleware_AppliesToLibraryMessages()
    {
        // The library's generator cannot see ApplicationTraceMiddleware, so this assembly's generator closes it over
        // the library's public messages as well as its own.
        var provider = BuildProvider(static x => x.AddMiddleware(typeof(ApplicationTraceMiddleware<,>)));
        var mediator = Mediator(provider);

        await mediator.Request<ApplicationPing, int>(new ApplicationPing(1));
        await mediator.Request<LibraryPing, int>(new LibraryPing(1));

        var entries = provider.GetRequiredService<TraceLog>().Entries;
        Assert.Contains("trace:ApplicationPing", entries);
        Assert.Contains("trace:LibraryPing", entries);
    }

    [Fact]
    public void ApplicationDeclaredOpenMiddleware_IsClosedOverPublicLibraryMessagesByGeneratedCode()
    {
        var services = CloseWithGeneratedCode(typeof(ApplicationPing).Assembly, typeof(StructResponseMiddleware<,>));
        Assert.Contains(services, static descriptor =>
            descriptor.ServiceType == typeof(IMiddleware<LibraryPing, int>) &&
            descriptor.ImplementationType == typeof(StructResponseMiddleware<LibraryPing, int>));

        Assert.DoesNotContain(services, static descriptor =>
            descriptor.ServiceType == typeof(IMiddleware<BaseCommand, string>));
    }

    [Fact]
    public async Task CrossAssemblyGapPass_ClosesMiddlewareOverMessagesOnlyTheirOwnAssemblyCanName()
    {
        // No generated code but the library's can name its internal stream, and the library cannot see this middleware,
        // so startup closes the pair with MakeGenericType under the JIT.
        var internalCount = typeof(LibraryPing).Assembly.GetType("Sharp.Mediator.LibraryFixtures.InternalLibraryCount", true)!;
        var generated = CloseWithGeneratedCode(typeof(ApplicationPing).Assembly, typeof(ApplicationStreamTraceMiddleware<,>));
        Assert.Contains(generated, static x => x.ServiceType == typeof(IStreamMiddleware<LibraryCount, int>));
        Assert.DoesNotContain(generated, x => x.ServiceType.GenericTypeArguments[0] == internalCount);

        var provider = BuildProvider(static x => x
            .AddHandlers(typeof(LibraryPing).Assembly)
            .AddMiddleware(typeof(ApplicationStreamTraceMiddleware<,>)));

        var request = Activator.CreateInstance(internalCount, 2)!;
        var items = new List<object?>();
        await foreach (var item in Mediator(provider).RequestStream(request)) items.Add(item);

        Assert.Equal([0, 1], items);
        Assert.Contains("stream:InternalLibraryCount", provider.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    public async Task CrossAssemblyGapPass_SkipsMessagesWhoseHandlerIsNotRegistered()
    {
        // Regression: the gap pass walked every handler any generated assembly declared, so the library's internal
        // stream, whose handler this container never registers, still got the middleware by reflection under the JIT
        // and failed startup under Native AOT.
        var services = GapServices(typeof(ApplicationStreamTraceMiddleware<,>));
        var provider = services.BuildServiceProvider();

        Assert.Empty(StreamMiddlewareOverInternalCount(services));

        var items = new List<int>();
        await foreach (var item in Mediator(provider).RequestStream(new LibraryCount(2))) items.Add(item);

        Assert.Equal([0, 1], items);
        Assert.Contains("stream:LibraryCount", provider.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    public void CrossAssemblyGapPass_HandlerRegisteredAfterAddMediator_FailsWhenTheMediatorIsCreated()
    {
        // The gap pass deferred the pair, and a built provider cannot take the middleware, so dispatching the handler
        // would silently skip it.
        var services = GapServices(typeof(ApplicationStreamTraceMiddleware<,>));
        RegisterInternalCountHandler(services);
        var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => Mediator(provider));
        Assert.Contains("InternalLibraryCount", exception.Message);
        Assert.Contains("ApplicationStreamTraceMiddleware", exception.Message);
        Assert.Contains("registered after AddMediator", exception.Message);
    }

    [Fact]
    public async Task CrossAssemblyGapPass_HandlerRegisteredBetweenAddMediatorCalls_IsClosedByTheLaterCall()
    {
        var services = GapServices(typeof(ApplicationStreamTraceMiddleware<,>));
        RegisterInternalCountHandler(services);
        services.AddMediator();
        var provider = services.BuildServiceProvider();

        Assert.Single(StreamMiddlewareOverInternalCount(services));

        var request = Activator.CreateInstance(InternalCount, 2)!;
        var items = new List<object?>();
        await foreach (var item in Mediator(provider).RequestStream(request)) items.Add(item);

        Assert.Equal([0, 1], items);
        Assert.Contains("stream:InternalLibraryCount", provider.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    public async Task CrossAssemblyGapPass_HandlerRegisteredAfterAddMediator_IsAcceptedWhenConstraintsExcludeIt()
    {
        // The middleware takes only streams of reference-type items, so the handler would not have had it anyway.
        var services = GapServices(typeof(ReferenceItemStreamMiddleware<,>));
        RegisterInternalCountHandler(services);
        var provider = services.BuildServiceProvider();

        var request = Activator.CreateInstance(InternalCount, 1)!;
        var items = new List<object?>();
        await foreach (var item in Mediator(provider).RequestStream(request)) items.Add(item);

        Assert.Equal([0], items);
        Assert.Empty(StreamMiddlewareOverInternalCount(services));
    }

    private static ServiceCollection GapServices(Type streamMiddleware)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TraceLog>();
        services.AddMediator(x => x.AddHandler<LibraryCountHandler>().AddMiddleware(streamMiddleware));
        return services;
    }

    // Only the library can name the handler, so register it the way its own code would, after the
    // application's AddMediator call.
    private static void RegisterInternalCountHandler(ServiceCollection services) =>
        services.AddTransient(
            typeof(IStreamRequestHandler<,>).MakeGenericType(InternalCount, typeof(int)),
            typeof(LibraryPing).Assembly.GetType("Sharp.Mediator.LibraryFixtures.InternalLibraryCountHandler", true)!);

    private static IEnumerable<ServiceDescriptor> StreamMiddlewareOverInternalCount(IEnumerable<ServiceDescriptor> services) =>
        services.Where(static x =>
            x.ServiceType.IsGenericType &&
            x.ServiceType.GetGenericTypeDefinition() == typeof(IStreamMiddleware<,>) &&
            x.ServiceType.GenericTypeArguments[0] == InternalCount);

    [Fact]
    public async Task PackagedMiddleware_AppliesToMessagesFromBothAssemblies()
    {
        // Both compilations reference the middleware package, which carries no registration metadata and was built
        // without the generator, so both generators find its public middleware and close it over their own messages.
        var provider = BuildProvider(static x => x.AddPackagedMiddleware());
        var mediator = Mediator(provider);

        await mediator.Request<ApplicationPing, int>(new ApplicationPing(1));
        await mediator.Request<LibraryPing, int>(new LibraryPing(1));

        var entries = provider.GetRequiredService<PackagedTrace>().Entries;
        Assert.Contains("packaged:ApplicationPing", entries);
        Assert.Contains("packaged:LibraryPing", entries);
    }

    [Fact]
    public void PackagedMiddleware_IsClosedByEachAssemblysGeneratedCode()
    {
        // Under the JIT the startup gap pass would close the same pairs with MakeGenericType, so the
        // descriptors alone prove nothing; calling each assembly's generated closer shows the pairs Native AOT
        // relies on were compiled in.
        var application = CloseWithGeneratedCode(typeof(ApplicationPing).Assembly, typeof(PackagedMiddleware<,>));
        var library = CloseWithGeneratedCode(typeof(LibraryPing).Assembly, typeof(PackagedMiddleware<,>));

        Assert.Contains(application, static x =>
            x.ServiceType == typeof(IMiddleware<ApplicationPing, int>) &&
            x.ImplementationType == typeof(PackagedMiddleware<ApplicationPing, int>));

        Assert.Contains(library, static x =>
            x.ServiceType == typeof(IMiddleware<LibraryPing, int>) &&
            x.ImplementationType == typeof(PackagedMiddleware<LibraryPing, int>));
    }

    // Runs the middleware closer the generator emitted into an assembly, as AddMediator does, on an empty collection.
    private static ServiceCollection CloseWithGeneratedCode(Assembly assembly, Type middlewareType)
    {
        const BindingFlags nonPublic = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var services = new ServiceCollection();
        var registration = typeof(MediatorRegistration).GetConstructors(nonPublic).Single().Invoke([services, ServiceLifetime.Transient]);

        var registrar = assembly.GetTypes().Single(static x => x.Name == "GeneratedMediatorRegistrar");
        registrar.GetMethod("CloseMiddleware", nonPublic)!.Invoke(null, [registration, middlewareType]);

        return services;
    }

    // ---- registration mechanics -------------------------------------------------------------------------------------

    [Fact]
    public void BothAssemblies_ContributeHandlers_WithIdempotentAddMediator()
    {
        var services = new ServiceCollection();
        services.AddMediator(static x => x.AddHandler<ApplicationPingHandler>().AddHandler<LibraryPingHandler>());
        services.AddMediator(static x => x.AddHandler<ApplicationPingHandler>().AddHandler<LibraryPingHandler>());
        Assert.Single(services, static x => x.ServiceType == typeof(IRequestHandler<ApplicationPing, int>));
        Assert.Single(services, static x => x.ServiceType == typeof(IRequestHandler<LibraryPing, int>));
        Assert.Single(services, static x => x.ServiceType == typeof(IMediator));
    }

    [Fact]
    public void AddMediatorTwice_DoesNotDuplicateMiddlewareRegistrations()
    {
        var services = new ServiceCollection();
        services.AddMediator(static x => x.AddHandler<ApplicationPingHandler>().AddPackagedMiddleware());
        services.AddMediator(static x => x.AddHandler<ApplicationPingHandler>().AddPackagedMiddleware());
        Assert.Single(services, static x =>
            x.ServiceType == typeof(IMiddleware<ApplicationPing, int>) &&
            x.ImplementationType == typeof(PackagedMiddleware<ApplicationPing, int>));
    }

    [Fact]
    public void AddHandlers_LibraryAssembly_UsesGeneratedRegistrationsOfEveryKind()
    {
        var services = new ServiceCollection();
        services.AddMediator(static x => x
            .AddHandler<ApplicationPingHandler>()
            .AddHandlers(typeof(LibraryPing).Assembly));

        Assert.Contains(services, static x => x.ImplementationType == typeof(LibraryPingHandler));
        Assert.Contains(services, static x => x.ImplementationType == typeof(LibraryEventHandler));
        Assert.Contains(services, static x => x.ImplementationType == typeof(LibraryCountHandler));
    }

    [Fact]
    public void AddHandlers_AssemblyBuiltWithoutTheGenerator_ThrowsWithGuidance()
    {
        // Regression: the reflection fallback registered handlers but no processors, so AddMediator succeeded and the
        // first dispatch failed with an unrelated DI error.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddMediator(static x => x.AddHandlers(typeof(NoGeneratorRequest).Assembly)));

        Assert.Contains("No source-generated handler registrations", exception.Message);
        Assert.Contains(typeof(NoGeneratorRequest).Assembly.GetName().Name!, exception.Message);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("instance")]
    [InlineData("factory of the handler")]
    // A handler registered in the collection directly for an event that no generated code declares would never run, and
    // since an event without a declared handler publishes to none, nothing would say so. AddMediator rejects one whose
    // registration names its class, and the first mediator rejects one registered after AddMediator.
    public void DirectlyRegisteredHandlerOfAnUndeclaredEvent_IsRejected(string registration)
    {
        void Register(ServiceCollection services)
        {
            switch (registration)
            {
                case "type": services.AddScoped<IEventHandler<NoGeneratorEvent>, NoGeneratorEventHandler>(); break;
                case "instance": services.AddSingleton<IEventHandler<NoGeneratorEvent>>(new NoGeneratorEventHandler()); break;
                default:
                    services.AddScoped<IEventHandler<NoGeneratorEvent>, NoGeneratorEventHandler>(static _ => new NoGeneratorEventHandler());
                    break;
            }
        }

        var before = new ServiceCollection();
        Register(before);

        var early = Assert.Throws<InvalidOperationException>(() =>
            before.AddMediator(static x => x.AddHandler<ApplicationPingHandler>()));

        var after = new ServiceCollection();
        after.AddMediator(static x => x.AddHandler<ApplicationPingHandler>());
        Register(after);

        using var provider = after.BuildServiceProvider();
        var late = Assert.Throws<InvalidOperationException>(() => provider.CreateScope().ServiceProvider.GetRequiredService<IMediator>());
        Assert.Contains(nameof(NoGeneratorEventHandler), early.Message);
        Assert.Contains(nameof(NoGeneratorEvent), late.Message);
    }

    [Fact]
    // A package built without the generator can still hand an application a handler for the application's own
    // events: registered for an event that some assembly declares a handler for, it is accepted and runs with
    // that event's handlers.
    public async Task DirectlyRegisteredHandlerFromAnAssemblyWithoutTheGenerator_RunsForADeclaredEvent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<LibraryTrace>();
        services.AddSingleton<PackagedTrace>();
        services.AddScoped<IEventHandler<LibraryEvent>, PackagedEventHandler<LibraryEvent>>();
        services.AddMediator(static x => x.AddHandler<LibraryEventHandler>());

        await using var provider = services.BuildServiceProvider();
        await provider.CreateScope().ServiceProvider.GetRequiredService<IMediator>().Publish(new LibraryEvent("x"));
        Assert.Equal(["packaged-event:LibraryEvent"], provider.GetRequiredService<PackagedTrace>().Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // A factory that names only the service type, or object, hides its handler, which cannot be checked.
    public void HandlerOfAnUndeclaredEventBehindAFactoryThatHidesIt_IsNotChecked(bool typedAsObject)
    {
        IServiceCollection services = new ServiceCollection();
        if (typedAsObject)
        {
            services.Add(new ServiceDescriptor(
                typeof(IEventHandler<NoGeneratorEvent>), static object (_) => new NoGeneratorEventHandler(), ServiceLifetime.Scoped));
        }
        else services.AddScoped<IEventHandler<NoGeneratorEvent>>(static _ => new NoGeneratorEventHandler());

        services.AddMediator(static x => x.AddHandler<ApplicationPingHandler>());
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.CreateScope().ServiceProvider.GetRequiredService<IMediator>());
    }

    [Fact]
    public void ExplicitHandlerWithoutGeneratedDispatch_FailsDuringConfiguration()
    {
        // An app declaring its own handlers has registrars, so nothing else stops this before the first dispatch.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddMediator(static x => x
                .AddHandler<ApplicationPingHandler>()
                .AddHandler<NoGeneratorRequestHandler>()));

        Assert.Contains(nameof(NoGeneratorRequestHandler), exception.Message);
        Assert.Contains(nameof(NoGeneratorRequest), exception.Message);
    }

    [Fact]
    public async Task AddHandlers_LibraryAssembly_DispatchesEndToEnd()
    {
        var services = new ServiceCollection();
        services.AddSingleton<LibraryTrace>();
        services.AddMediator(static x => x
            .AddHandler<ApplicationPingHandler>()
            .AddHandlers(typeof(LibraryPing).Assembly));

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        Assert.Equal(50, await mediator.Request<LibraryPing, int>(new LibraryPing(5)));
        Assert.Equal(50, await mediator.Request((object)new LibraryPing(5)));
    }

    // ---- decided semantics pinned -----------------------------------------------------------------------------------

    [Fact]
    public async Task Publish_EventTypeWithNoHandlerAnywhere_PublishesToNone()
    {
        // An event type no compilation declared a handler for threw, so an event published before anything handled it,
        // or by a module whose handlers were not loaded, failed. It publishes to no handlers now, as MediatR does and
        // as an event whose declared handlers are not registered already did.
        var mediator = Mediator(BuildProvider());
        await mediator.Publish(new OrphanEvent());
        await mediator.Publish((object)new OrphanEvent());
    }

    [Fact]
    public async Task ObjectRequest_UnknownType_ThrowsWithGuidance()
    {
        var mediator = Mediator(BuildProvider());
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => mediator.Request(new object()).AsTask());
        Assert.Contains("is not a message", exception.Message);
    }
}