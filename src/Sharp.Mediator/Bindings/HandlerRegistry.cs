using System.ComponentModel;
using System.Reflection;

namespace Sharp.Mediator.Bindings;

/// <summary>
/// Hook through which source-generated code supplies trim/AOT-safe handler registrations for a compilation. Generated code calls
/// <see cref="AddRegistrar" /> from a <c>[ModuleInitializer]</c>; the builder's <see cref="MediatorBuilder.AddHandlers(Assembly)" /> drains the
/// matching registrar instead of reflecting over the assembly. This process-lifetime registry retains assembly references, so collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext" /> instances are not supported. Infrastructure for the Sharp.Mediator source generator; not
/// for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HandlerRegistry
{
    private static readonly Lock Gate = new();
    private static readonly List<(Assembly Assembly, Action<MediatorRegistration> Register)> Registrars = [];

    /// <summary>Called by generated code (from a module initializer) to contribute one assembly's handler registrations.</summary>
    public static void AddRegistrar(Assembly assembly, Action<MediatorRegistration> register)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(register);

        using (Gate.EnterScope())
        {
            Registrars.Add((assembly, register));
        }
    }

    /// <summary>
    /// Combines every registrar contributed for <paramref name="assembly" /> into one callback. Returns <c>false</c> when none ran — i.e. the generator
    /// wasn't applied to that assembly.
    /// </summary>
    internal static bool TryGetFor(Assembly assembly, out Action<MediatorRegistration>? register)
    {
        Action<MediatorRegistration>? combined = null;

        using (Gate.EnterScope())
        {
            foreach (var (candidate, registrar) in Registrars)
                if (candidate == assembly)
                    combined = combined is null ? registrar : combined + registrar;
        }

        register = combined;
        return combined is not null;
    }
}