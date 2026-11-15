using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;

namespace Sharp.Mediator;

public partial class MediatorBuilder
{
    private const string DynamicMiddlewareMessage =
        "Closes open-generic middleware with Type.MakeGenericType over the messages no assembly's source generator " +
        "could close it over, which needs runtime code generation. Under NativeAOT those are messages only their own " +
        "assembly can name, such as internal ones, met by middleware that assembly cannot see: make the messages " +
        "public, declare the middleware public in an assembly the handler assemblies reference, or register the " +
        "closed middleware types.";

    private const DynamicallyAccessedMemberTypes MiddlewareDynamicallyAccessedMembers =
        DynamicallyAccessedMemberTypes.Interfaces | DynamicallyAccessedMemberTypes.PublicConstructors;

    private readonly List<MiddlewareEntry> _middlewareEntries = [];

    /// <summary>
    /// Registers a middleware: a type that implements <see cref="IMiddleware{TMessage}" />, <see cref="IMiddleware{TMessage, TResponse}" /> or
    /// <see cref="IStreamMiddleware{TRequest, TResponse}" />. A closed type is registered under every closed middleware interface it implements; an
    /// open-generic definition is closed over every applicable message type at build time, by the source generator of each assembly that can name both the
    /// middleware and the message. A message no generator could close it over, such as an internal one in an assembly that cannot see the middleware, is
    /// closed by reflection at startup under the JIT, and fails startup under Native AOT, once its handler is registered. Open definitions must mirror the
    /// relevant middleware interface's complete generic parameter list one-for-one in the same order; register closed types for fixed, reordered, unused,
    /// or generic-containing-type shapes.
    /// </summary>
    public MediatorBuilder AddMiddleware<[DynamicallyAccessedMembers(MiddlewareDynamicallyAccessedMembers)] TMiddleware>()
        where TMiddleware : class =>
        AddMiddleware(typeof(TMiddleware));

    /// <inheritdoc cref="AddMiddleware{TMiddleware}()" />
    public MediatorBuilder AddMiddleware([DynamicallyAccessedMembers(MiddlewareDynamicallyAccessedMembers)] Type middlewareType)
    {
        ArgumentNullException.ThrowIfNull(middlewareType);
        if (!middlewareType.IsClass || middlewareType.IsAbstract)
            throw new InvalidOperationException($"Middleware type({middlewareType.Name}) must be a concrete class.");

        if (middlewareType.IsGenericTypeDefinition)
        {
            var serviceDefinitions = GetOpenMiddlewareServiceDefinitions(middlewareType);
            if (serviceDefinitions.Length == 0)
                throw new InvalidOperationException(MissingInterfaceMessage(middlewareType));

            _middlewareEntries.Add(new MiddlewareEntry(middlewareType, serviceDefinitions));

            return this;
        }

        if (middlewareType.ContainsGenericParameters)
        {
            throw new InvalidOperationException(
                $"Middleware type({middlewareType.Name}) must be a closed type or an open-generic type definition.");
        }

        // Closed middleware needs no source generation at all: its interfaces are already closed, so registering them
        // here is trim/AOT-safe and — unlike per-compilation generated registrations — sees every interface the type
        // implements and works for middleware declared in any assembly.
        var closedRegistrations = middlewareType.GetInterfaces()
            .Where(static x => x.IsGenericType && IsMiddlewareServiceDefinition(x.GetGenericTypeDefinition()))
            .Select(x => (ServiceType: x, ImplementationType: middlewareType))
            .ToArray();

        if (closedRegistrations.Length == 0)
            throw new InvalidOperationException(MissingInterfaceMessage(middlewareType));

        _middlewareEntries.Add(new MiddlewareEntry(closedRegistrations));

        return this;
    }

    private static Type[] GetOpenMiddlewareServiceDefinitions(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type openGenericDefinition
    )
    {
        var parameters = openGenericDefinition.GetGenericArguments();
        var interfaces = openGenericDefinition.GetInterfaces()
            .Where(static x => x.IsGenericType && IsMiddlewareServiceDefinition(x.GetGenericTypeDefinition()))
            .ToArray();

        foreach (var middlewareInterface in interfaces)
        {
            var arguments = middlewareInterface.GetGenericArguments();
            if (arguments.Length != parameters.Length || !arguments.SequenceEqual(parameters))
            {
                throw new InvalidOperationException(
                    $"Open-generic middleware type({openGenericDefinition}) is not supported. Its generic " +
                    "parameters must match the middleware interface parameters one-for-one in the same order, " +
                    "and it cannot be nested in a generic containing type. Register closed middleware types instead.");
            }
        }

        return [.. interfaces.Select(static x => x.GetGenericTypeDefinition()).Distinct()];
    }

    private static bool IsMiddlewareServiceDefinition(Type interfaceDefinition) =>
        interfaceDefinition == typeof(IMiddleware<>) || interfaceDefinition == typeof(IMiddleware<,>) ||
        interfaceDefinition == typeof(IStreamMiddleware<,>);

    private static string MissingInterfaceMessage(Type middlewareType) =>
        $"Middleware type({middlewareType.Name}) must implement IMiddleware<TMessage>, IMiddleware<TMessage, TResponse> " +
        "or IStreamMiddleware<TRequest, TResponse>.";

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification =
        "Every closed middleware registration originates from a DynamicallyAccessedMembers-annotated AddMiddleware " +
        "parameter (Interfaces | PublicConstructors); the requirement is enforced at each insertion site.")]
    [UnconditionalSuppressMessage("Trimming", "IL2077", Justification =
        "Same guarantee in its field-flow form: the implementation type is read back from a (Type, Type)[] tuple " +
        "field, which cannot carry the PublicConstructors annotation validated at the AddMiddleware insertion site.")]
    // Applies middleware in registration order: closed registrations directly, open generics via every generated closer
    // (each covers its own compilation's messages) plus a gap pass over the registered handler services so
    // cross-assembly combinations are closed too. TryAddEnumerable everywhere makes middleware registration idempotent
    // across repeated AddMediator calls and duplicate Add* calls.
    // Entries accumulate in the service collection's history and are all reapplied: an assembly's registrar only
    // appears once its module initializer has run, so a later AddMediator call can introduce messages that middleware
    // from an earlier call must still cover. Returns the pairs the gap pass left open because their handler was not
    // registered yet, which ValidateDeferredGaps checks against the registrations a provider is built from.
    internal List<DeferredGap> ApplyMiddlewareRegistrations(MediatorRegistration registration, List<MiddlewareEntry> history)
    {
        history.AddRange(_middlewareEntries);
        List<DeferredGap> deferredGaps = [];
        foreach (var entry in history)
        {
            if (entry.ClosedRegistrations is { } closedRegistrations)
            {
                foreach (var (serviceType, implementationType) in closedRegistrations)
                    registration.TryAddEnumerable(serviceType, implementationType);

                continue;
            }

            var middlewareType = entry.OpenGenericDefinition!;
            foreach (var closer in MediatorRuntime.MiddlewareClosers)
                closer(registration, middlewareType);

            CloseRemainingPairs(registration, middlewareType, entry.ServiceDefinitions!, deferredGaps);
        }

        return deferredGaps;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification =
        "CloseGapsViaReflection only runs under the JIT (RuntimeFeature.IsDynamicCodeSupported is checked first); " +
        "under NativeAOT the gap check throws with guidance before any reflection.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification =
        "CloseGapsViaReflection only runs under the JIT (RuntimeFeature.IsDynamicCodeSupported is checked first); " +
        "under NativeAOT the gap check throws with guidance before any reflection.")]
    // Cross-assembly gap pass: candidates come from the declared handler services (which span every contributing
    // assembly); anything no generated closer marked as considered is a combination no single compilation could see.
    // Only a registered handler needs it now: closed via reflection under the JIT, reported with guidance under Native
    // AOT. A declared handler nothing registered yet, such as a referenced library's internal one the application never
    // uses, is deferred instead, so it neither fails a Native AOT startup nor costs reflection under the JIT.
    private static void CloseRemainingPairs(
        MediatorRegistration registration,
        Type middlewareType,
        Type[] serviceDefinitions,
        List<DeferredGap> deferredGaps
    )
    {
        List<(Type ServiceDefinition, Type[] Arguments)> missingPairs = [];
        foreach (var serviceDefinition in serviceDefinitions)
        foreach (var handlerService in registration.DeclaredHandlerServices)
        {
            if (!CoversHandlers(serviceDefinition, handlerService.GetGenericTypeDefinition())) continue;

            var arguments = handlerService.GenericTypeArguments;
            if (registration.IsMiddlewareConsidered(
                    middlewareType, serviceDefinition, arguments[0],
                    arguments.Length > 1 ? arguments[1] : null))
                continue;

            if (registration.IsRegistered(handlerService)) missingPairs.Add((serviceDefinition, arguments));
            else deferredGaps.Add(new DeferredGap(middlewareType, serviceDefinition, arguments, handlerService));
        }

        if (missingPairs.Count == 0) return;

        EnsureRuntimeClosingAvailable(middlewareType, missingPairs);
        CloseGapsViaReflection(registration, middlewareType, missingPairs);
    }

    /// <summary>
    /// Fails when <paramref name="services" /> now registers the handler of a pair the gap pass deferred: it was registered after the last
    /// <see cref="MediatorServiceCollectionExtensions.AddMediator" /> call, and a provider cannot take the middleware that call would have closed over it,
    /// so the handler would otherwise run without it.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification =
        "TryCloseViaReflection only runs under the JIT: under NativeAOT EnsureRuntimeClosingAvailable throws first for " +
        "every deferred pair whose handler is registered.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification =
        "TryCloseViaReflection only runs under the JIT: under NativeAOT EnsureRuntimeClosingAvailable throws first for " +
        "every deferred pair whose handler is registered.")]
    internal static void ValidateDeferredGaps(IServiceCollection services, List<DeferredGap> deferredGaps)
    {
        if (deferredGaps.Count == 0) return;

        var registered = MediatorRegistration.RegisteredServiceTypes(services);
        List<DeferredGap> lateGaps = [.. deferredGaps.Where(x => registered.Contains(x.HandlerService))];
        foreach (var group in lateGaps.GroupBy(static x => x.MiddlewareType))
            EnsureRuntimeClosingAvailable(group.Key, [.. group.Select(static x => (x.ServiceDefinition, x.Arguments))]);

        foreach (var gap in lateGaps)
        {
            // A message the middleware's generic constraints exclude would not have been given it anyway.
            if (!TryCloseViaReflection(gap.MiddlewareType, gap.Arguments, out _)) continue;

            throw new InvalidOperationException(
                $"Handler service '{gap.HandlerService}' was registered after AddMediator, so open-generic middleware " +
                $"'{gap.MiddlewareType}' was not closed over its message, and the handler would run without it. " +
                "AddMediator closes middleware that no source generator could close over a message only for the " +
                "handlers registered by the time it runs. Register the handler before calling AddMediator, or call " +
                "AddMediator again after registering it.");
        }
    }

    [ExcludeFromCodeCoverage(Justification =
        "RuntimeFeature.IsDynamicCodeSupported is always true under the JIT coverage runner. The NativeAOT canary " +
        "executes this guard in a native binary and verifies the message type and valid Type<,> registration guidance.")]
    private static void EnsureRuntimeClosingAvailable(
        Type middlewareType,
        List<(Type ServiceDefinition, Type[] Arguments)> missingPairs
    )
    {
        if (RuntimeFeature.IsDynamicCodeSupported) return;

        var middlewareName = middlewareType.Name;
        var tick = middlewareName.IndexOf('`');
        if (tick >= 0) middlewareName = middlewareName[..tick];

        var arity = middlewareType.GetGenericArguments().Length;
        var middlewareSyntax = arity == 0
            ? middlewareName
            : $"{middlewareName}<{new string(',', arity - 1)}>";

        throw new InvalidOperationException(
            $"Open-generic middleware '{middlewareType}' could not be closed over " +
            $"[{string.Join(", ", missingPairs.Select(static x => x.Arguments[0].Name))}] at build time, and runtime " +
            "closing is unavailable under NativeAOT. A source generator closes middleware over a message only where it " +
            "can name both: in the assembly that declares the handler, for its own middleware and the public middleware " +
            "of the assemblies it references, and in an assembly that references the handler's, for the middleware it " +
            "sees, when the message is public. Make those messages public, declare the middleware public in an " +
            "assembly that the assemblies declaring those handlers reference, or register the closed middleware types " +
            $"in place of typeof({middlewareSyntax}), which is also the only option for messages its generic " +
            "constraints exclude.");
    }

    private static bool CoversHandlers(Type serviceDefinition, Type handlerDefinition)
    {
        if (handlerDefinition == typeof(IRequestHandler<>) || handlerDefinition == typeof(IEventHandler<>))
            return serviceDefinition == typeof(IMiddleware<>);

        return serviceDefinition == (handlerDefinition == typeof(IRequestHandler<,>)
            ? typeof(IMiddleware<,>)
            : typeof(IStreamMiddleware<,>));
    }

    [RequiresUnreferencedCode(DynamicMiddlewareMessage)]
    [RequiresDynamicCode(DynamicMiddlewareMessage)]
    internal static void CloseGapsViaReflection(
        MediatorRegistration registration,
        Type middlewareType,
        List<(Type ServiceDefinition, Type[] Arguments)> missingPairs
    )
    {
        foreach (var (serviceDefinition, arguments) in missingPairs)
        {
            if (!TryCloseViaReflection(middlewareType, arguments, out var implementation)) continue;

            var service = serviceDefinition.MakeGenericType(arguments);
            if (!service.IsAssignableFrom(implementation))
            {
                throw new InvalidOperationException(
                    $"Middleware closure invariant failed: '{implementation}' does not implement '{service}'.");
            }

            registration.TryAddEnumerable(service, implementation);
        }
    }

    [RequiresUnreferencedCode(DynamicMiddlewareMessage)]
    [RequiresDynamicCode(DynamicMiddlewareMessage)]
    private static bool TryCloseViaReflection(
        Type middlewareType,
        Type[] arguments,
        [NotNullWhen(true)] out Type? implementation
    )
    {
        try
        {
            implementation = middlewareType.MakeGenericType(arguments);
            return true;
        }
        catch (ArgumentException)
        {
            // The type arguments violate the middleware's generic constraints; it does not apply to this message.
            implementation = null;
            return false;
        }
    }

    /// <summary>
    /// A middleware-and-message pair the gap pass left open because no handler of <see cref="HandlerService" /> was registered when
    /// <see cref="MediatorServiceCollectionExtensions.AddMediator" /> ran.
    /// </summary>
    internal readonly record struct DeferredGap(
        Type MiddlewareType,
        Type ServiceDefinition,
        Type[] Arguments,
        Type HandlerService
    );

    /// <summary>One ordered middleware registration: precomputed closed pairs, or an open-generic definition to close.</summary>
    internal readonly struct MiddlewareEntry
    {
        public MiddlewareEntry(Type openGenericDefinition, Type[] serviceDefinitions)
        {
            OpenGenericDefinition = openGenericDefinition;
            ServiceDefinitions = serviceDefinitions;
        }

        public MiddlewareEntry((Type ServiceType, Type ImplementationType)[] closedRegistrations) => ClosedRegistrations = closedRegistrations;

        public readonly Type? OpenGenericDefinition;
        public readonly Type[]? ServiceDefinitions;
        public readonly (Type ServiceType, Type ImplementationType)[]? ClosedRegistrations;
    }
}