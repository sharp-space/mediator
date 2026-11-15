using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator;

/// <summary>Service-collection extensions for wiring up <see cref="IMediator" />.</summary>
public static class MediatorServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the mediator and, via the source generator, the closed-generic registrations for every message type in the compilation. Use
        /// <paramref name="configure" /> to register handlers, middleware, the publisher, and the lifetime. The first call fixes the lifetime and
        /// builder-selected publisher for this service collection; later calls inherit those values and may add registrations, but explicitly conflicting global
        /// settings are rejected. An <see cref="IEventPublisher" /> descriptor registered directly before this call keeps precedence.
        /// </summary>
        public IServiceCollection AddMediator(Action<MediatorBuilder>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            var state = services
                .LastOrDefault(static x => x.ServiceType == typeof(MediatorConfigurationState))?
                .ImplementationInstance as MediatorConfigurationState;

            var builder = state is null
                ? new MediatorBuilder()
                : new MediatorBuilder(state.Lifetime, state.PublisherType, state.ExclusiveOptions);

            configure?.Invoke(builder);
            if (state is null)
            {
                services.Add(ServiceDescriptor.Singleton(
                    state = new MediatorConfigurationState(
                        builder.Lifetime, builder.PublisherType ?? typeof(SequentialPublisher), builder.ExclusiveOptions)));
            }
            else
                state.ValidateAndMerge(builder);

            var lifetime = state.Lifetime;
            var registration = new MediatorRegistration(services, lifetime);
            builder.ApplyPendingRegistrations(registration);
            ValidateSingleHandlerServices(services);
            ValidateDeclaredEventHandlers(services, () => registration.DeclaredHandlerServices);

            services.TryAdd(new ServiceDescriptor(typeof(IEventPublisher), state.PublisherType, lifetime));

            if (!MediatorRuntime.HasAssemblies)
            {
                throw new InvalidOperationException(
                    "No generated mediator registrar was found. Reference the Sharp.Mediator source generator " +
                    "(it ships inside the Sharp.Mediator package) and define at least one handler so its " +
                    "registrations can be generated.");
            }

            builder.ValidateHandlers(registration);

            // Middleware is applied in registration order after every registrar has contributed its handler services
            // and middleware closers: closed middleware registers its implemented interfaces directly (AOT-safe), and
            // each open generic is closed by every contributing assembly's closer plus a cross-assembly gap pass over
            // the handlers registered by now, which replaces what the previous call deferred.
            state.DeferredGaps = builder.ApplyMiddlewareRegistrations(registration, state.MiddlewareHistory);

            // A scoped service under a Singleton mediator fails here when it is already registered, and otherwise when
            // a provider creates its first mediator, which reads the registrations as they then stand. So does a
            // handler registered after this call that needs middleware the gap pass deferred. Each provider reads them
            // once, in its ServiceRetention singleton, and its mediators take that answer: one collection can build
            // several providers and change between them.
            var retention = ServiceRetention.Of(services, lifetime);
            services.TryAdd(ServiceDescriptor.Singleton(
                typeof(ServiceRetention), provider => state.RetentionOf(services).For(provider)));

            services.TryAdd(DefaultMediator.Descriptor(lifetime, RuntimeFeature.IsDynamicCodeCompiled));

            // Forwarded so a scope has one mediator, and one chain cache, however a class chooses to depend on it.
            services.TryAdd(new ServiceDescriptor(typeof(IRequester), x => x.GetRequiredService<IMediator>(), lifetime));
            services.TryAdd(new ServiceDescriptor(typeof(IPublisher), x => x.GetRequiredService<IMediator>(), lifetime));

            // This call has checked its handlers above, and the pairs its gap pass deferred have no handler registered
            // yet. The descriptors it added since then are no handler, middleware or publisher, so a provider built
            // from the registrations as they now stand takes this answer rather than reading every registration again;
            // it tells that they still stand by comparing them with the descriptors recorded here.
            state.Checked(retention, services);

            return services;
        }
    }

    private static void ValidateSingleHandlerServices(IServiceCollection services)
    {
        foreach (var group in services
                     .Where(static descriptor => !descriptor.IsKeyedService && IsSingleHandlerService(descriptor.ServiceType))
                     .GroupBy(static descriptor => descriptor.ServiceType))
        {
            var descriptors = group.ToArray();
            if (descriptors.Length < 2) continue;

            var implementations = string.Join(", ", descriptors.Select(DescribeImplementation));
            throw new InvalidOperationException(
                $"Handler service '{group.Key}' has multiple registrations: {implementations}. " +
                "Requests and streams require exactly one handler implementation.");
        }
    }

    // A handler registered in the collection runs for a publish only when generated code declares a handler for its
    // event type, and an event that none is declared for publishes to none without a word. A handler in an assembly
    // compiled with the generator is declared, or reported at build time as SMD003, SMD006 or SMD007, so only one in an
    // assembly compiled without it, such as a test double or a proxy, is checked here: rejected when no assembly
    // declares its service either. Its assembly is initialized first, since registering a type runs nothing of its
    // assembly. A factory that names only the service type hides its handler, which is not checked.
    private static void ValidateDeclaredEventHandlers(IServiceCollection services, Func<HashSet<Type>> declared)
    {
        List<(Type Service, Type Implementation)>? handlers = null;
        Dictionary<Assembly, bool>? compiledWithGenerator = null;
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService || descriptor.ServiceType is not { IsConstructedGenericType: true } service ||
                service.GetGenericTypeDefinition() != typeof(IEventHandler<>) ||
                NamedImplementation(descriptor) is not { } implementation)
                continue;

            var assembly = implementation.Assembly;
            compiledWithGenerator ??= [];
            if (!compiledWithGenerator.TryGetValue(assembly, out var generated))
            {
                MediatorBuilder.InitializeModuleOf(assembly);
                compiledWithGenerator[assembly] = generated = HandlerRegistry.TryGetFor(assembly, out _);
            }

            if (!generated) (handlers ??= []).Add((service, implementation));
        }

        if (handlers is null) return;

        var declaredServices = declared();
        foreach (var (service, implementation) in handlers)
            if (!declaredServices.Contains(service))
            {
                throw new InvalidOperationException(
                    $"Event handler '{implementation}' is registered for '{service}', but its assembly was not compiled with " +
                    "the Sharp.Mediator source generator, and no assembly that was declares a handler for event type " +
                    $"'{service.GenericTypeArguments[0]}', so publishing that event would never reach it. Declare a " +
                    "handler for the event in an assembly compiled with the generator, which ships in the Sharp.Mediator " +
                    "package, or compile the handler's assembly with it.");
            }
    }

    // The class a registration names as its implementation, or null when a factory names only the service type.
    private static Type? NamedImplementation(ServiceDescriptor descriptor)
    {
        var named = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
        if (named is not null) return named;

        var declared = descriptor.ImplementationFactory!.GetType().GenericTypeArguments[1];
        return !declared.IsInterface && descriptor.ServiceType.IsAssignableFrom(declared) ? declared : null;
    }

    private static bool IsSingleHandlerService(Type type)
    {
        if (!type.IsGenericType) return false;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IRequestHandler<>) ||
            definition == typeof(IRequestHandler<,>) ||
            definition == typeof(IStreamRequestHandler<,>);
    }

    private static string DescribeImplementation(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType?.ToString() ??
        descriptor.ImplementationInstance?.GetType().ToString() ??
        descriptor.ImplementationFactory!.Method.ToString()!;

    private sealed class MediatorConfigurationState(
        ServiceLifetime lifetime,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type publisherType,
        IReadOnlyDictionary<string, string> exclusiveOptions
    )
    {
        private readonly Dictionary<string, string> _exclusiveOptions = CopyExclusiveOptions(exclusiveOptions);

        private Retained? _retained;

        public ServiceLifetime Lifetime { get; } = lifetime;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type PublisherType { get; } = publisherType;

        public IReadOnlyDictionary<string, string> ExclusiveOptions => _exclusiveOptions;

        /// <summary>Every middleware registered on this service collection so far, in registration order.</summary>
        public List<MediatorBuilder.MiddlewareEntry> MiddlewareHistory { get; } = [];

        /// <summary>The middleware pairs the last AddMediator call left open because their handler was not registered.</summary>
        public List<MediatorBuilder.DeferredGap> DeferredGaps { get; set; } = [];

        /// <summary>Records what AddMediator found for the registrations as they stand when it returns.</summary>
        public void Checked(ServiceRetention retention, IServiceCollection services) =>
            Volatile.Write(ref _retained, new Retained(retention, [.. services]));

        /// <summary>
        /// What a mediator over <paramref name="services" /> may keep, after checking that each request and stream still has one handler and that no handler the
        /// gap pass deferred has been registered since. Each provider asks once, when its first mediator is created, so registrations made after AddMediator
        /// count too, and so do those changed between two providers built from the collection. A registration replaced, as a test host's ConfigureTestServices
        /// replaces one, keeps their number, so the last answer holds only while the descriptors it was found for stand as they were, which comparing references
        /// tells for a small part of what reading them again costs.
        /// </summary>
        public ServiceRetention RetentionOf(IServiceCollection services)
        {
            if (Volatile.Read(ref _retained) is { } retained && Unchanged(services, retained.Descriptors))
                return retained.Retention;

            ValidateSingleHandlerServices(services);
            ValidateDeclaredEventHandlers(services, MediatorRuntime.DeclaredHandlerServices);
            MediatorBuilder.ValidateDeferredGaps(services, DeferredGaps);
            var retention = ServiceRetention.Of(services, Lifetime);
            Volatile.Write(ref _retained, new Retained(retention, [.. services]));
            return retention;
        }

        private static bool Unchanged(IServiceCollection services, ServiceDescriptor[] descriptors)
        {
            if (services.Count != descriptors.Length) return false;

            for (var i = 0; i < descriptors.Length; i++)
                if (!ReferenceEquals(services[i], descriptors[i]))
                    return false;

            return true;
        }

        public void ValidateAndMerge(MediatorBuilder builder)
        {
            if (builder.LifetimeConfigured && builder.Lifetime != Lifetime)
            {
                throw new InvalidOperationException(
                    $"AddMediator already fixed the service lifetime to '{Lifetime}'. " +
                    $"A later call cannot change it to '{builder.Lifetime}'.");
            }

            if (builder.PublisherConfigured && builder.PublisherType != PublisherType)
            {
                throw new InvalidOperationException(
                    $"AddMediator already fixed the event publisher to '{PublisherType}'. " +
                    $"A later call cannot change it to '{builder.PublisherType}'.");
            }

            foreach (var (key, value) in builder.ExclusiveOptions)
                _exclusiveOptions[key] = value;
        }

        private static Dictionary<string, string> CopyExclusiveOptions(IReadOnlyDictionary<string, string> options)
        {
            var copy = new Dictionary<string, string>(options.Count, StringComparer.Ordinal);
            foreach (var (key, value) in options) copy.Add(key, value);

            return copy;
        }

        // The last answer and the descriptors it was found for.
        private sealed record Retained(ServiceRetention Retention, ServiceDescriptor[] Descriptors);
    }
}