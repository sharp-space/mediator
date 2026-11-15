using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator;

// What a mediator may keep beyond the dispatch that resolved it. A mediator caches a message's chain, and its event
// publisher, for as long as the mediator lives, so it keeps only services registered with at least its own lifetime
// and resolves a shorter-lived one for each dispatch: caching never changes what a registration's lifetime means. A
// scoped service under a Singleton mediator cannot be honored at all, because the mediator resolves from the root
// provider, and neither can a disposable transient one there, because the root provider keeps every disposable
// instance it creates. It also says from which miss a mediator of its lifetime caches, so that a container creates a
// mediator from it alone.
internal sealed class ServiceRetention
{
    private const int ScopedWarmAfterMisses = 8;

    // Closed handler and middleware services, and open generic definitions of them, that live shorter
    // than the mediator.
    private readonly HashSet<Type> _shortLived;

    // Whether _shortLived holds an open generic definition, the only case in which a service can be short-lived by its
    // definition, which takes a call and another search to look up.
    private readonly bool _shortLivedDefinitions;

    private ServiceRetention(
        HashSet<Type> shortLived,
        bool keepsPublisher,
        int warmAfterMisses,
        MiddlewareKinds registeredMiddleware
    )
    {
        _shortLived = shortLived;
        _shortLivedDefinitions = shortLived.Any(static x => x.IsGenericTypeDefinition);
        KeepsPublisher = keepsPublisher;
        WarmAfterMisses = warmAfterMisses;
        RegisteredMiddleware = registeredMiddleware;
    }

    /// <summary>The kinds of middleware the registrations provide, closed or open generic.</summary>
    public MiddlewareKinds RegisteredMiddleware { get; }

    /// <summary>Misses before the mediator starts caching; 0 never caches.</summary>
    public int WarmAfterMisses { get; }

    /// <summary>Whether the mediator may keep the event publisher it resolves.</summary>
    public bool KeepsPublisher { get; }

    /// <summary>Whether any handler, middleware or publisher lives shorter than the mediator.</summary>
    public bool IsRestricted => _shortLived.Count != 0 || !KeepsPublisher;

    // Microsoft's container resolves only what the service collection registers, so under it a kind of middleware the
    // collection does not register cannot be resolved at all. Another container may also resolve services registered
    // through its own API, which a mediator that looked up no other kind would skip without a word, so under one the
    // mediator looks up every kind.
    /// <summary>This answer as a mediator over <paramref name="provider" /> takes it.</summary>
    public ServiceRetention For(IServiceProvider provider) =>
        provider.GetType().Assembly.GetName().Name == "Microsoft.Extensions.DependencyInjection"
            ? this
            : new ServiceRetention(_shortLived, KeepsPublisher, WarmAfterMisses, MiddlewareKinds.All);

    /// <summary>Whether the mediator may keep a chain built from these handler and middleware services.</summary>
    public bool Keeps(Type handlerService, Type middlewareService) =>
        !IsShortLived(handlerService) && !IsShortLived(middlewareService);

    private bool IsShortLived(Type service) =>
        _shortLived.Contains(service) ||
        (_shortLivedDefinitions && _shortLived.Contains(service.GetGenericTypeDefinition()));

    /// <summary>
    /// The retention for a mediator of <paramref name="mediatorLifetime" /> over <paramref name="services" />. Throws when the mediator is a singleton and a
    /// handler, middleware or publisher is scoped, or transient with a disposable implementation.
    /// </summary>
    public static ServiceRetention Of(IServiceCollection services, ServiceLifetime mediatorLifetime)
    {
        HashSet<Type> shortLived = [];
        var keepsPublisher = true;
        List<Type>? captive = null;
        List<Type>? disposable = null;
        var registeredMiddleware = MiddlewareKinds.None;
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService || !IsMediatorService(descriptor.ServiceType)) continue;

            registeredMiddleware |= KindOf(descriptor.ServiceType);
            switch (mediatorLifetime)
            {
                // ServiceLifetime orders Singleton, Scoped, Transient, from the longest-lived.
                case ServiceLifetime.Singleton when descriptor.Lifetime == ServiceLifetime.Scoped:
                    (captive ??= []).Add(descriptor.ServiceType);
                    break;
                case ServiceLifetime.Singleton when descriptor.Lifetime == ServiceLifetime.Transient &&
                    DeclaredImplementation(descriptor) is var implementation && IsDisposable(implementation):
                    (disposable ??= []).Add(implementation);
                    break;
                case ServiceLifetime.Scoped:
                case ServiceLifetime.Transient:
                default:
                {
                    if (descriptor.Lifetime > mediatorLifetime && descriptor.ServiceType == typeof(IEventPublisher))
                        keepsPublisher = false;
                    else if (descriptor.Lifetime > mediatorLifetime)
                        shortLived.Add(descriptor.ServiceType);

                    break;
                }
            }
        }

        if (captive is not null)
        {
            throw new InvalidOperationException(
                "The mediator is registered as Singleton, but these services are registered as Scoped: " +
                $"{string.Join(", ", captive.Distinct().Select(static x => $"'{x}'"))}. A Singleton mediator resolves its " +
                "handlers, middleware and publisher from the root provider, where a scoped service would live as long as the " +
                "process. Register them as Singleton or, unless they are disposable, Transient, or the mediator as Scoped " +
                "with WithLifetime.");
        }

        if (disposable is not null)
        {
            throw new InvalidOperationException(
                "The mediator is registered as Singleton, but these Transient services are disposable: " +
                $"{string.Join(", ", disposable.Distinct().Select(static x => $"'{x}'"))}. A Singleton mediator resolves a " +
                "Transient handler, middleware or publisher from the root provider for each dispatch, and the root provider " +
                "keeps every disposable instance it creates until it is disposed itself. Register them as Singleton, or the " +
                "mediator as Scoped with WithLifetime.");
        }

        // Caching is automatic and lifetime-aware. A Singleton mediator lives for the process, so it caches from its
        // first dispatch. A Scoped one usually serves one request, so it waits until the scope proves busy. Transient
        // bypasses the cache so each call resolves a fresh handler.
        var warmAfterMisses = mediatorLifetime switch
        {
            ServiceLifetime.Singleton => 1,
            ServiceLifetime.Scoped => ScopedWarmAfterMisses,
            _ => 0
        };

        return new ServiceRetention(shortLived, keepsPublisher, warmAfterMisses, registeredMiddleware);
    }

    private static MiddlewareKinds KindOf(Type service)
    {
        if (!service.IsGenericType) return MiddlewareKinds.None;

        var definition = service.GetGenericTypeDefinition();
        return definition == typeof(IMiddleware<>) ? MiddlewareKinds.Message :
            definition == typeof(IMiddleware<,>) ? MiddlewareKinds.Request :
            definition == typeof(IStreamMiddleware<,>) ? MiddlewareKinds.Stream : MiddlewareKinds.None;
    }

    // The type a transient registration says it creates: its implementation type, or the type its factory is declared
    // to return, which is the service type itself when the factory names no other.
    private static Type DeclaredImplementation(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType ?? descriptor.ImplementationFactory!.GetType().GenericTypeArguments[1];

    // The root provider keeps what it would dispose, and it disposes either kind.
    private static bool IsDisposable(Type type) =>
        typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type);

    private static bool IsMediatorService(Type type)
    {
        if (type == typeof(IEventPublisher)) return true;
        if (!type.IsGenericType) return false;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IRequestHandler<>) || definition == typeof(IRequestHandler<,>) ||
            definition == typeof(IEventHandler<>) || definition == typeof(IStreamRequestHandler<,>) ||
            definition == typeof(IMiddleware<>) || definition == typeof(IMiddleware<,>) ||
            definition == typeof(IStreamMiddleware<,>);
    }
}

/// <summary>Kinds of middleware, by the interface a middleware implements.</summary>
[Flags]
internal enum MiddlewareKinds : byte
{
    /// <summary>No middleware.</summary>
    None = 0,

    /// <summary><see cref="IMiddleware{TMessage}" />, of void requests and events.</summary>
    Message = 1,

    /// <summary><see cref="IMiddleware{TRequest, TResponse}" />, of requests with a response.</summary>
    Request = 2,

    /// <summary><see cref="IStreamMiddleware{TRequest, TResponse}" />, of stream requests.</summary>
    Stream = 4,

    /// <summary>Every kind.</summary>
    All = Message | Request | Stream
}