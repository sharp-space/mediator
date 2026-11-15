using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Sharp.Mediator;

/// <summary>Fluent configuration surface passed to <see cref="MediatorServiceCollectionExtensions.AddMediator" />.</summary>
/// <remarks>
/// Handler registrations are collected and applied when <see cref="MediatorServiceCollectionExtensions.AddMediator" /> returns from the configure
/// callback, all with the final lifetime — so <see cref="WithLifetime" /> may be called at any point during configuration without affecting earlier
/// <c>Add*</c> calls. Validation still happens at each <c>Add*</c> call site.
/// </remarks>
public sealed partial class MediatorBuilder
{
    private readonly Dictionary<string, string> _exclusiveOptions;

    internal MediatorBuilder(
        ServiceLifetime lifetime = ServiceLifetime.Scoped,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type? publisherType = null,
        IReadOnlyDictionary<string, string>? exclusiveOptions = null
    )
    {
        Lifetime = lifetime;
        PublisherType = publisherType;
        _exclusiveOptions = new Dictionary<string, string>(exclusiveOptions?.Count ?? 0, StringComparer.Ordinal);

        if (exclusiveOptions is null) return;

        foreach (var (key, value) in exclusiveOptions)
            _exclusiveOptions.Add(key, value);
    }

    // Not public: within a configure callback it reads the default until a later WithLifetime call changes it, so an
    // extension that read it could act on a lifetime the mediator never gets.
    internal ServiceLifetime Lifetime { get; private set; }

    internal bool LifetimeConfigured { get; private set; }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    internal Type? PublisherType { get; private set; }

    internal bool PublisherConfigured { get; private set; }

    internal IReadOnlyDictionary<string, string> ExclusiveOptions => _exclusiveOptions;

    /// <summary>
    /// Selects <paramref name="value" /> for an option that allows only one value per service collection, across repeated
    /// <see cref="MediatorServiceCollectionExtensions.AddMediator" /> calls. For packages that extend the builder.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A different value was already selected for <paramref name="key" />; the message is <paramref name="conflictMessage" />.
    /// </exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public MediatorBuilder WithExclusiveOption(string key, string value, string conflictMessage)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(conflictMessage);
        if (_exclusiveOptions.TryGetValue(key, out var selected) && selected != value)
            throw new InvalidOperationException(conflictMessage);

        _exclusiveOptions[key] = value;

        return this;
    }

    /// <summary>Overrides the lifetime used for every registration produced by this builder, regardless of call order.</summary>
    public MediatorBuilder WithLifetime(ServiceLifetime lifetime)
    {
        Lifetime = lifetime;
        LifetimeConfigured = true;

        return this;
    }

    /// <summary>Replaces the default <see cref="IEventPublisher" /> (which is <see cref="Publishers.SequentialPublisher" />).</summary>
    public MediatorBuilder WithEventPublisher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPublisher>()
        where TPublisher : IEventPublisher
    {
        PublisherType = typeof(TPublisher);
        PublisherConfigured = true;

        return this;
    }
}