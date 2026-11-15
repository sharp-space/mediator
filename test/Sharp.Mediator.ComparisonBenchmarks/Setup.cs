namespace Sharp.Mediator.ComparisonBenchmarks;

using Fixtures;
using global::Mediator;
using Microsoft.Extensions.DependencyInjection;
using SharpMessage = Sharp.FirstMessage;
using MediatRMessage = MediatR.FirstMessage;

/// <summary>Builds each library's provider on the same lifetime, with the same optional pass-through step.</summary>
internal static class Setup
{
#if SCOPED_LIFETIME
    public const ServiceLifetime Lifetime = ServiceLifetime.Scoped;
#else
    public const ServiceLifetime Lifetime = ServiceLifetime.Singleton;
#endif

    // Indirect Sharp.Mediator's AddMediator through a delegate so Mediator's source generator does not try to parse
    // this site as one of its own configuration calls.
    private static readonly Func<IServiceCollection, Action<MediatorBuilder>?, IServiceCollection> SharpAddMediator =
        MediatorServiceCollectionExtensions.AddMediator;

    public static ServiceProvider Sharp(bool withMiddleware = false)
    {
        var services = new ServiceCollection();
        SharpAddMediator(services, builder =>
        {
            builder.WithLifetime(Lifetime).AddHandlers(typeof(SharpMessage).Assembly);
            if (withMiddleware) builder.AddMiddleware(typeof(SharpPassThrough<,>));
        });

        return services.BuildServiceProvider();
    }

    public static ServiceProvider Martin(bool withBehavior = false)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddMediator();
        if (withBehavior)
            services.Add(new ServiceDescriptor(typeof(IPipelineBehavior<,>), typeof(MartinPassThrough<,>), Lifetime));

        // A comparison across lifetimes would still run and print numbers, so refuse it here.
        var generated = services.First(static x => x.ServiceType == typeof(Mediator)).Lifetime;
        if (generated != Lifetime)
        {
            throw new InvalidOperationException(
                $"Mediator was generated for {generated}, but this comparison is configured for {Lifetime}.");
        }

        return services.BuildServiceProvider();
    }

    // MediatR registers its mediator and the handlers it scans for on the lifetime it is given, and the behavior on the
    // same lifetime, as the other two libraries keep theirs.
    public static ServiceProvider MediatR(bool withBehavior = false)
    {
        var services = new ServiceCollection();
        services.AddMediatR(configuration =>
        {
            configuration.Lifetime = Lifetime;
            configuration.RegisterServicesFromAssembly(typeof(MediatRMessage).Assembly);
            if (withBehavior) configuration.AddOpenBehavior(typeof(MediatRPassThrough<,>), Lifetime);
        });

        return services.BuildServiceProvider();
    }
}