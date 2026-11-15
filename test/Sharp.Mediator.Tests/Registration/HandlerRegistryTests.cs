using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;

namespace Sharp.Mediator.Tests.Registration;

// Covers the source-generated handler and middleware registration:
//  - HandlerRegistry (null guards + multi-registrar combine via AddHandlers)
//  - builder.AddHandlers(Assembly) source-generated hit
//
// All paths use this (generated) assembly. The JIT reflection fallback (no generator) is JIT-only and excluded from
// coverage, so it has no dedicated test.
public sealed class HandlerRegistryTests
{
    private static readonly Assembly GeneratedAssembly = typeof(HandlerRegistryTests).Assembly;

    // ---- HandlerRegistry --------------------------------------------------------------------------------------------

    [Fact]
    public void AddRegistrar_NullAssembly_Throws() =>
        Assert.Throws<ArgumentNullException>(() => HandlerRegistry.AddRegistrar(null!, static _ => { }));

    [Fact]
    public void AddRegistrar_NullRegister_Throws() =>
        Assert.Throws<ArgumentNullException>(() => HandlerRegistry.AddRegistrar(GeneratedAssembly, null!));

    [Fact]
    public void AddHandlers_CombinesMultipleRegistrarsForSameAssembly()
    {
        // Two registrars for the same assembly must both run (Delegate.Combine path in TryGetFor). The generated
        // registrar registered at startup already covers _generatedAssembly; add a second and confirm both fire.
        var (list, saved) = SnapshotRegistry();

        try
        {
            var extraRan = false;
            HandlerRegistry.AddRegistrar(GeneratedAssembly, _ => extraRan = true);

            var services = new ServiceCollection();
            services.AddMediator(x => x.AddHandlers(GeneratedAssembly));

            Assert.True(extraRan);
            Assert.Contains(services, d => d.ImplementationType == typeof(EchoRequestHandler));
        }
        finally
        {
            RestoreRegistry(list, saved);
        }
    }

    // ---- AddHandlers: source-generated hit (this assembly) ----------------------------------------------------------

    [Fact]
    public void AddHandlers_GeneratedAssembly_UsesGeneratedRegistrations()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandlers(GeneratedAssembly));
        Assert.Contains(services, d => d.ImplementationType == typeof(EchoRequestHandler));
        Assert.Contains(services, d => d.ImplementationType == typeof(FirstEventHandler));
        Assert.Contains(services, d => d.ImplementationType == typeof(StreamRequestHandler));
    }

    [Fact]
    public void AddHandlers_UsesConfiguredLifetime()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.WithLifetime(ServiceLifetime.Singleton).AddHandlers(GeneratedAssembly));

        var descriptor = services.First(d => d.ImplementationType == typeof(EchoRequestHandler));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public async Task AddHandlers_GeneratedAssembly_DispatchesEndToEnd()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddHandlers(GeneratedAssembly));

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        Assert.Equal("echo:hi", await mediator.Request<EchoRequest, string>(new EchoRequest("hi")));
    }

    private static (IList List, object[] Saved) SnapshotRegistry()
    {
        var field = typeof(HandlerRegistry).GetField("Registrars", BindingFlags.NonPublic | BindingFlags.Static)!;
        var list = (IList)field.GetValue(null)!;
        var saved = new object[list.Count];
        list.CopyTo(saved, 0);
        return (list, saved);
    }

    private static void RestoreRegistry(IList list, object[] saved)
    {
        list.Clear();
        foreach (var item in saved) list.Add(item);
    }
}