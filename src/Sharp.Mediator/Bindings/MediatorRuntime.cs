using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator.Bindings;

/// <summary>
/// Hook through which source-generated code describes its compilation. Generated code calls <see cref="AddAssembly" /> from a <c>[ModuleInitializer]</c>;
/// <see cref="MediatorServiceCollectionExtensions.AddMediator" /> reads the result through <see cref="MediatorRegistration" />.
/// </summary>
/// <remarks>
/// Contributions accumulate so handlers generated into any number of assemblies all count. The registry is process-lifetime and retains their delegates;
/// assemblies loaded into collectible <see cref="System.Runtime.Loader.AssemblyLoadContext" /> instances are therefore not supported. Infrastructure for
/// the Sharp.Mediator source generator; not for direct use.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MediatorRuntime
{
    // The generated-code contract this version reads, which generated code passes to AddAssembly first. A later version
    // that changes what generated code contributes reads the contracts it supersedes as well, by this number, rather
    // than replacing a member that generated code already calls.
    private const int Contract = 1;
    private static readonly Lock Gate = new();
    private static readonly List<Func<Type[]>> HandlerServiceFactories = [];
    private static readonly List<MiddlewareCloser> RegisteredMiddlewareClosers = [];

    private static int _slotCount;

    internal static bool HasAssemblies
    {
        get
        {
            lock (Gate)
            {
                return HandlerServiceFactories.Count > 0;
            }
        }
    }

    private static Func<Type[]>[] HandlerServices
    {
        get
        {
            lock (Gate)
            {
                return [.. HandlerServiceFactories];
            }
        }
    }

    internal static MiddlewareCloser[] MiddlewareClosers
    {
        get
        {
            lock (Gate)
            {
                return [.. RegisteredMiddlewareClosers];
            }
        }
    }

    // From 1: no message takes slot 0, where a mediator's array holds what it caches past the array's end.
    internal static int NextSlot() => Interlocked.Increment(ref _slotCount);

    /// <summary>
    /// Called by generated code, once per assembly. <paramref name="contract" /> is the generated-code contract the code was generated for, which this
    /// version of Sharp.Mediator must read, and <paramref name="assembly" /> the assembly it was generated into. <paramref name="requests" />,
    /// <paramref name="events" /> and <paramref name="streams" /> map an exact message type to a new route, or null when the assembly does not declare it.
    /// <paramref name="eventInterfaces" /> adds the interfaces an event is that the assembly declares event handlers for, and is null when it declares none.
    /// <paramref name="handlerServices" /> lists the closed handler services the compilation can dispatch, and is read only when a handler was registered by
    /// type or an open-generic middleware has to be closed, so a startup with neither loads none of it. <paramref name="closeMiddleware" /> is null when the
    /// compilation sees no open-generic middleware.
    /// </summary>
    public static void AddAssembly(
        int contract,
        Assembly assembly,
        Func<Type, object?> requests,
        Func<Type, object?> events,
        Action<object, ISet<Type>>? eventInterfaces,
        Func<Type, object?> streams,
        Func<Type[]> handlerServices,
        MiddlewareCloser? closeMiddleware
    )
    {
        // Thrown from a module initializer, which a stack trace does not tie to its assembly under Native AOT, so the
        // message names it.
        ArgumentNullException.ThrowIfNull(assembly);
        if (contract != Contract)
        {
            throw new InvalidOperationException(
                $"The generated code of assembly '{assembly.GetName().Name}' was generated for contract {contract}, which " +
                $"this version of Sharp.Mediator does not read: it reads contract {Contract}. Build that assembly against " +
                "the version of Sharp.Mediator the application runs.");
        }

        ArgumentNullException.ThrowIfNull(handlerServices);
        MessageRoutes.AddResolvers(requests, events, eventInterfaces, streams);

        lock (Gate)
        {
            HandlerServiceFactories.Add(handlerServices);
            if (closeMiddleware is not null) RegisteredMiddlewareClosers.Add(closeMiddleware);
        }
    }

    /// <summary>Every closed handler service generated code can dispatch, across the assemblies initialized so far.</summary>
    internal static HashSet<Type> DeclaredHandlerServices() => [.. HandlerServices.SelectMany(static x => x())];
}

/// <summary>
/// Closes one registered open-generic middleware definition over the closed message types a generated compilation knows about. Contributed via
/// <see cref="MediatorRuntime.AddAssembly" />; invoked by <see cref="MediatorServiceCollectionExtensions.AddMediator" /> once per registered
/// open-generic middleware, in registration order.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate void MiddlewareCloser(MediatorRegistration registration, Type middlewareType);

/// <summary>
/// Context of one <see cref="MediatorServiceCollectionExtensions.AddMediator" /> call, passed to generated handler registrars and middleware closers.
/// Carries the lifetime the generated, AOT-safe registrations are applied with. Generated code only calls <see cref="TryAddEnumerable" />,
/// <see cref="TryAddClosedMiddleware{TService,TImplementation}" /> and <see cref="MarkMiddlewareConsidered" />, so nothing else is public.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class MediatorRegistration
{
    private readonly HashSet<ConsideredPair> _consideredPairs = [];
    private HashSet<(Type Service, Type Implementation)>? _registeredPairs;
    private HashSet<Type>? _registeredServices;

    internal MediatorRegistration(IServiceCollection services, ServiceLifetime lifetime)
    {
        Services = services;
        Lifetime = lifetime;
    }

    /// <summary>The service collection to register into.</summary>
    private IServiceCollection Services { get; }

    /// <summary>Lifetime configured via <see cref="MediatorBuilder.WithLifetime" />.</summary>
    private ServiceLifetime Lifetime { get; }

    /// <summary>Every closed handler service generated code can dispatch, across all contributing assemblies.</summary>
    internal HashSet<Type> DeclaredHandlerServices => field ??= MediatorRuntime.DeclaredHandlerServices();

    /// <summary>
    /// Registers <paramref name="implementationType" /> for <paramref name="serviceType" /> with <see cref="Lifetime" /> unless the collection already holds
    /// that pair without a key, as
    /// <see
    ///     cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)" />
    /// does. That method scans the collection on every call, which grows with the square of the handler count; this one checks a set of the collection taken
    /// once per call of <see cref="MediatorServiceCollectionExtensions.AddMediator" />. Infrastructure for the Sharp.Mediator source generator; not for
    /// direct use.
    /// </summary>
    public void TryAddEnumerable(
        Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType
    )
    {
        _registeredPairs ??= RegisteredPairs(Services);
        if (_registeredPairs.Add((serviceType, implementationType)))
            Services.Add(new ServiceDescriptor(serviceType, implementationType, Lifetime));
    }

    // The implementation type TryAddEnumerable compares, derived the way it derives it: declared, from the instance, or
    // from the factory's return type. A keyed descriptor never matches an unkeyed registration.
    private static HashSet<(Type Service, Type Implementation)> RegisteredPairs(IServiceCollection services)
    {
        var pairs = new HashSet<(Type Service, Type Implementation)>(services.Count);
        foreach (var descriptor in services)
            if (!descriptor.IsKeyedService)
            {
                pairs.Add((descriptor.ServiceType,
                    descriptor.ImplementationType ??
                    descriptor.ImplementationInstance?.GetType() ??
                    descriptor.ImplementationFactory!.GetType().GenericTypeArguments[1]));
            }

        return pairs;
    }

    /// <summary>
    /// Called by generated code to record that it evaluated closing <paramref name="middlewareType" /> as <paramref name="closedServiceType" /> — whether it
    /// registered the pair or excluded it because the message does not satisfy the middleware's generic constraints. Pairs never marked are the
    /// cross-assembly gaps <see cref="MediatorServiceCollectionExtensions.AddMediator" /> closes via reflection under the JIT (or reports under Native AOT);
    /// "considered" rather than "closed" so a constraint-excluded pair is not mistaken for a gap. Infrastructure for the Sharp.Mediator source generator; not
    /// for direct use.
    /// </summary>
    public void MarkMiddlewareConsidered(Type middlewareType, Type closedServiceType)
    {
        var arguments = closedServiceType.GenericTypeArguments;
        _consideredPairs.Add(new ConsideredPair(
            middlewareType,
            closedServiceType.GetGenericTypeDefinition(),
            arguments[0],
            arguments.Length > 1 ? arguments[1] : null));
    }

    /// <summary>
    /// Called by generated code for each pair it closes: registers <typeparamref name="TImplementation" /> for <typeparamref name="TService" /> as
    /// <see cref="TryAddEnumerable" /> does, and marks the pair considered for <paramref name="middlewareType" /> as <see cref="MarkMiddlewareConsidered" />
    /// does. Infrastructure for the Sharp.Mediator source generator; not for direct use.
    /// </summary>
    // ReSharper disable once UnusedMember.Global — only the registration code the generator emits calls this method.
    public void TryAddClosedMiddleware<
        TService, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        Type middlewareType
    )
    {
        TryAddEnumerable(typeof(TService), typeof(TImplementation));
        MarkMiddlewareConsidered(middlewareType, typeof(TService));
    }

    /// <summary>
    /// Whether the collection holds an unkeyed registration of <paramref name="serviceType" />, as it stood when this was first asked: the gap pass asks
    /// after every handler of this call is registered.
    /// </summary>
    internal bool IsRegistered(Type serviceType) =>
        (_registeredServices ??= RegisteredServiceTypes(Services)).Contains(serviceType);

    /// <summary>The service types <paramref name="services" /> registers without a key, which are the ones dispatch resolves.</summary>
    internal static HashSet<Type> RegisteredServiceTypes(IServiceCollection services)
    {
        var types = new HashSet<Type>(services.Count);
        foreach (var descriptor in services)
            if (!descriptor.IsKeyedService)
                types.Add(descriptor.ServiceType);

        return types;
    }

    internal bool IsMiddlewareConsidered(Type middlewareType, Type serviceDefinition, Type argument0, Type? argument1) =>
        _consideredPairs.Contains(new ConsideredPair(middlewareType, serviceDefinition, argument0, argument1));

    // Considered pairs are stored decomposed (definition + arguments) so the runtime gap check can compare candidates
    // derived from declared handler services without constructing closed service types — constructing them would need
    // MakeGenericType, which is not available under Native AOT.

    // ReSharper disable NotAccessedPositionalProperty.Local — every member is part of the _consideredPairs key: the
    // generated Equals and GetHashCode read them, and nothing reads them by name.
    private readonly record struct ConsideredPair(
        Type MiddlewareType,
        Type ServiceDefinition,
        Type Argument0,
        Type? Argument1
    );
    // ReSharper restore NotAccessedPositionalProperty.Local
}