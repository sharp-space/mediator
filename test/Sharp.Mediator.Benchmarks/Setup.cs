using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Benchmarks.Fixtures;
using Sharp.Mediator.ScaleFixtures;

namespace Sharp.Mediator.Benchmarks;

/// <summary>Builds the DI providers the benchmark scenarios dispatch through.</summary>
internal static class Setup
{
    public static IServiceProvider Sharp(bool withMiddleware = false)
    {
        var services = new ServiceCollection();

        services.AddMediator(builder =>
        {
            builder.WithLifetime(ServiceLifetime.Singleton)
                .AddHandler<PingHandler>()
                .AddHandler<Ping2Handler>()
                .AddHandler<Ping3Handler>()
                .AddHandler<Ping4Handler>()
                .AddHandler<NoopHandler>()
                .AddHandler<FirstSignalHandler>()
                .AddHandler<SecondSignalHandler>()
                .AddHandler<CountHandler>();

            if (withMiddleware)
                builder.AddMiddleware(typeof(PassThroughMiddleware<,>));
        });

        return services.BuildServiceProvider();
    }

    /// <summary>Sharp with the Scoped lifetime — caching is automatic and per-scope. Resolve the mediator within a scope.</summary>
    public static IServiceProvider SharpScoped(bool withMiddleware = false)
    {
        var services = new ServiceCollection();

        services.AddMediator(builder =>
        {
            builder.WithLifetime(ServiceLifetime.Scoped)
                .AddHandler<PingHandler>()
                .AddHandler<Ping2Handler>()
                .AddHandler<Ping3Handler>()
                .AddHandler<Ping4Handler>()
                .AddHandler<NoopHandler>()
                .AddHandler<FirstSignalHandler>()
                .AddHandler<SecondSignalHandler>()
                .AddHandler<CountHandler>();

            if (withMiddleware)
                builder.AddMiddleware(typeof(PassThroughMiddleware<,>));
        });

        return services.BuildServiceProvider();
    }

    /// <summary>Sharp with the Singleton lifetime and only what <paramref name="configure" /> registers.</summary>
    public static IServiceProvider SharpWith(Action<MediatorBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddMediator(builder => configure(builder.WithLifetime(ServiceLifetime.Singleton)));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Sharp with only Ping's handler, registered by hand with a lifetime of its own, under a mediator of <paramref name="mediator" />, which keeps the
    /// handler only if it lives at least as long as the mediator.
    /// </summary>
    public static IServiceProvider SharpLifetimes(ServiceLifetime mediator, ServiceLifetime handler)
    {
        IServiceCollection services = new ServiceCollection();
        services.Add(new ServiceDescriptor(
            typeof(IRequestHandler<Ping, int>),
            typeof(PingHandler),
            handler));

        services.AddMediator(builder => builder.WithLifetime(mediator));
        return services.BuildServiceProvider();
    }

    /// <summary>Sharp with the large message set from the ScaleFixtures project, and open-generic middleware if asked.</summary>
    public static IServiceProvider SharpScale(ServiceLifetime lifetime, bool withMiddleware = false)
    {
        var services = new ServiceCollection();
        services.AddMediator(builder =>
        {
            builder.WithLifetime(lifetime).AddHandlers(typeof(FirstMessage).Assembly);
            if (withMiddleware) builder.AddMiddleware(typeof(PassThroughMiddleware<,>));
        });

        return services.BuildServiceProvider();
    }
}