using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sharp.Mediator.Bindings;

namespace Sharp.Mediator;

public partial class MediatorBuilder
{
    private const DynamicallyAccessedMemberTypes HandlerDynamicallyAccessedMembers =
        DynamicallyAccessedMemberTypes.Interfaces | DynamicallyAccessedMemberTypes.PublicConstructors;

    private readonly List<PendingRegistration> _pendingRegistrations = [];

    /// <summary>
    /// Registers a handler under every <see cref="IRequestHandler{TRequest}" />, <see cref="IRequestHandler{TRequest, TResponse}" />,
    /// <see cref="IEventHandler{TEvent}" /> and <see cref="IStreamRequestHandler{TRequest, TResponse}" /> interface it implements.
    /// </summary>
    public MediatorBuilder AddHandler<[DynamicallyAccessedMembers(HandlerDynamicallyAccessedMembers)] THandler>()
        where THandler : class =>
        AddHandler(typeof(THandler));

    /// <inheritdoc cref="AddHandler{THandler}()" />
    public MediatorBuilder AddHandler([DynamicallyAccessedMembers(HandlerDynamicallyAccessedMembers)] Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        if (!IsRegistrableConcreteType(handlerType))
            throw new InvalidOperationException($"Handler type({handlerType.Name}) must be a non-abstract, non-generic class.");

        var serviceTypes = handlerType.GetInterfaces().Where(static interfaceType =>
        {
            if (!interfaceType.IsGenericType) return false;

            var genericTypeDefinition = interfaceType.GetGenericTypeDefinition();
            return genericTypeDefinition == typeof(IRequestHandler<>)
                || genericTypeDefinition == typeof(IRequestHandler<,>)
                || genericTypeDefinition == typeof(IEventHandler<>)
                || genericTypeDefinition == typeof(IStreamRequestHandler<,>);
        }).ToArray();

        return serviceTypes.Length == 0
            ? throw new InvalidOperationException(
                $"Handler type({handlerType.Name}) must implement IRequestHandler<TRequest>, " +
                "IRequestHandler<TRequest, TResponse>, IEventHandler<TEvent> or IStreamRequestHandler<TRequest, TResponse>.")
            : EnqueueHandlerRegistrations(handlerType, serviceTypes);
    }

    /// <summary>
    /// Registers every handler the source generator discovered in <paramref name="assembly" />, using its closed-generic, trim/AOT-safe registrations.
    /// Throws when the generator was not applied to the assembly (it ships in the package's analyzer slot, so C# projects get it by default): dispatch
    /// routes only come from generated code, so handlers compiled without it could never be dispatched.
    /// </summary>
    public MediatorBuilder AddHandlers(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EnsureModuleInitialized(assembly);
        if (!HandlerRegistry.TryGetFor(assembly, out var register))
        {
            throw new InvalidOperationException(
                $"No source-generated handler registrations were found for assembly '{assembly.GetName().Name}'. " +
                "Handlers must be compiled by C# with the Sharp.Mediator source generator, which ships in the " +
                "Sharp.Mediator package's analyzer slot. Reference that package from the assembly; projects that " +
                "disable analyzers or use another language cannot declare handlers.");
        }

        _pendingRegistrations.Add(new PendingRegistration(register!));

        return this;
    }

    // For a handler registered in the service collection directly, whose assembly nothing else initializes.
    internal static void InitializeModuleOf(Assembly assembly) => EnsureModuleInitialized(assembly);

    [ExcludeFromCodeCoverage(Justification =
        "RuntimeFeature.IsDynamicCodeSupported is always true under the JIT coverage runner; the NativeAOT canary " +
        "executes the other branch in a native binary.")]
    // The generated registrar signs up from a [ModuleInitializer], which the JIT only guarantees to have run once a
    // member of the module has been accessed. Passing an Assembly does not count as such an access (e.g. one obtained
    // purely via reflection), so force it deterministically. Native AOT runs module initializers eagerly.
    private static void EnsureModuleInitialized(Assembly assembly)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification =
        "Every implementation type in the pending list originates from a DynamicallyAccessedMembers-annotated " +
        "AddHandler parameter (Interfaces | PublicConstructors); the requirement is enforced at each insertion site.")]
    [UnconditionalSuppressMessage("Trimming", "IL2077", Justification =
        "Same guarantee in its field-flow form: the implementation type is read back from a (Type, Type)[] tuple " +
        "field, which cannot carry the PublicConstructors annotation validated at the AddHandler insertion site.")]
    // Handler registrations are deferred so they all use the final Lifetime; the implementation types were validated
    // (and their handler interfaces computed) at the Add* call site, where the DynamicallyAccessedMembers annotation
    // was still attached.
    internal void ApplyPendingRegistrations(MediatorRegistration registration)
    {
        foreach (var pending in _pendingRegistrations)
        {
            if (pending.Register is { } register)
            {
                register(registration);
                continue;
            }

            foreach (var (serviceType, implementationType) in pending.HandlerRegistrations!)
                registration.TryAddEnumerable(serviceType, implementationType);
        }
    }

    private MediatorBuilder EnqueueHandlerRegistrations(Type handlerType, Type[] serviceTypes)
    {
        EnsureModuleInitialized(handlerType.Assembly);
        _pendingRegistrations.Add(new PendingRegistration(
            [.. serviceTypes.Select(x => (x, handlerType))]));

        return this;
    }

    // An explicitly registered handler goes undeclared when its assembly was built without the generator or its message
    // is inaccessible to generated code (SMD003). Report that here rather than at the first dispatch.
    internal void ValidateHandlers(MediatorRegistration registration)
    {
        foreach (var (serviceType, implementationType) in _pendingRegistrations.SelectMany(static x => x.HandlerRegistrations ?? []))
            if (!registration.DeclaredHandlerServices.Contains(serviceType))
            {
                throw new InvalidOperationException(
                    $"Handler '{implementationType}' has no generated dispatch for message type " +
                    $"'{serviceType.GenericTypeArguments[0]}'. Its assembly must be compiled by C# with the " +
                    "Sharp.Mediator source generator, and generated code must be able to access the message " +
                    "(see SMD003).");
            }
    }

    private static bool IsRegistrableConcreteType(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsGenericType: false };

    /// <summary>One deferred registration: either precomputed handler descriptors or a generated-registrar drain.</summary>
    private readonly struct PendingRegistration
    {
        public PendingRegistration((Type ServiceType, Type ImplementationType)[] handlerRegistrations) => HandlerRegistrations = handlerRegistrations;

        public PendingRegistration(Action<MediatorRegistration> register) => Register = register;

        public readonly (Type ServiceType, Type ImplementationType)[]? HandlerRegistrations;

        public readonly Action<MediatorRegistration>? Register;
    }
}