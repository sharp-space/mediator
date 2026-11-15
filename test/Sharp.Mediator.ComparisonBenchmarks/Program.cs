using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.ComparisonBenchmarks.Scenarios;
using MartinMediator = Mediator;
using MediatRMediator = MediatR;
using SharpFirst = Sharp.Mediator.ComparisonBenchmarks.Sharp.FirstEvent;
using SharpLast = Sharp.Mediator.ComparisonBenchmarks.Sharp.LastEvent;
using MartinFirst = Sharp.Mediator.ComparisonBenchmarks.Martin.FirstEvent;
using MartinLast = Sharp.Mediator.ComparisonBenchmarks.Martin.LastEvent;
using MediatRFirst = Sharp.Mediator.ComparisonBenchmarks.MediatR.FirstEvent;
using MediatRLast = Sharp.Mediator.ComparisonBenchmarks.MediatR.LastEvent;

namespace Sharp.Mediator.ComparisonBenchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--check")
        {
            await SmokeCheck();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, BenchmarkConfig());
    }

    private static IConfig BenchmarkConfig() =>
        DefaultConfig.Instance.WithArtifactsPath(ArtifactsPath());

    private static string ArtifactsPath()
    {
        var path = typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static x => x.Key == "BenchmarkDotNetArtifactsPath")
            ?.Value;

        return Path.GetFullPath(path ?? "BenchmarkDotNet.Artifacts");
    }

    // Runs every scenario's setup and benchmark methods once, without timing them, and fails when the libraries would
    // not do the same work.
    private static async Task SmokeCheck()
    {
        Console.WriteLine($"Lifetime: {Setup.Lifetime}");

        // The publish scenarios compare the same work only while every library reaches the same handlers for the first
        // and the last event.
        var handlers = (
            Sharp: HandlerCounts<IEventHandler<SharpFirst>, IEventHandler<SharpLast>>(Setup.Sharp()),
            Mediator:
            HandlerCounts<MartinMediator.INotificationHandler<MartinFirst>, MartinMediator.INotificationHandler<MartinLast>>(Setup.Martin()),
            MediatR: HandlerCounts<MediatRMediator.INotificationHandler<MediatRFirst>, MediatRMediator.INotificationHandler<MediatRLast>>(
                Setup.MediatR()));

        if (handlers != ((2, 2), (2, 2), (2, 2)))
            throw new InvalidOperationException($"The publish scenarios reach different numbers of handlers: {handlers}.");

        Console.WriteLine("Publish handlers: 2 for each library's first and last event");

        var dispatch = new DispatchBenchmarks();
        dispatch.Setup();
        await dispatch.Sharp_Interface_First();
        await dispatch.Sharp_Interface_Last();
        await dispatch.Sharp_Object_Last();
        await dispatch.Mediator_Interface_First();
        await dispatch.Mediator_Interface_Last();
        await dispatch.Mediator_Object_Last();
        await dispatch.Mediator_Concrete_First();
        await dispatch.Mediator_Concrete_Last();
        await dispatch.MediatR_Interface_First();
        await dispatch.MediatR_Interface_Last();
        await dispatch.MediatR_Object_Last();
        dispatch.Cleanup();
        Console.WriteLine("Dispatch: ok");

        var voidRequest = new VoidRequestBenchmarks();
        voidRequest.Setup();
        await voidRequest.Sharp_First();
        await voidRequest.Sharp_Last();
        await voidRequest.Mediator_First();
        await voidRequest.Mediator_Last();
        await voidRequest.Mediator_Concrete_First();
        await voidRequest.Mediator_Concrete_Last();
        await voidRequest.MediatR_First();
        await voidRequest.MediatR_Last();
        voidRequest.Cleanup();
        Console.WriteLine("Void request: ok");

        var publish = new PublishBenchmarks();
        publish.Setup();
        await publish.Sharp_First();
        await publish.Sharp_Last();
        await publish.Mediator_First();
        await publish.Mediator_Last();
        await publish.Mediator_Concrete_First();
        await publish.Mediator_Concrete_Last();
        await publish.MediatR_First();
        await publish.MediatR_Last();
        publish.Cleanup();
        Console.WriteLine("Publish: ok");

        var mixed = new MixedTrafficBenchmarks();
        mixed.Setup();
        Console.WriteLine(
            $"Mixed: {mixed.Sharp_Interface()} {mixed.Mediator_Interface()} {mixed.Mediator_Concrete()} " +
            $"{mixed.MediatR_Interface()}");

        mixed.Cleanup();

        var middleware = new MiddlewareBenchmarks();
        middleware.Setup();
        await middleware.Sharp();
        await middleware.Mediator();
        await middleware.Mediator_Concrete();
        await middleware.MediatR();
        middleware.Cleanup();
        Console.WriteLine("Middleware: ok");

        var requestScope = new RequestScopeBenchmarks();
        requestScope.Setup();
        await requestScope.Sharp();
        await requestScope.Mediator();
        await requestScope.MediatR();
        requestScope.Cleanup();
        Console.WriteLine("Request scope: ok");

        var coldStart = new ColdStartBenchmarks();
        await coldStart.Sharp();
        await coldStart.Mediator();
        await coldStart.MediatR();
        Console.WriteLine("Cold start: ok");

        var firstDispatch = new FirstDispatchBenchmarks();
        firstDispatch.Setup();
        await firstDispatch.Sharp();
        await firstDispatch.Mediator();
        await firstDispatch.MediatR();
        firstDispatch.Cleanup();
        Console.WriteLine("First dispatch: ok");
    }

    // Counted in a scope, so the Scoped lifetime resolves its handlers as the scenarios do.
    private static (int First, int Last) HandlerCounts<TFirst, TLast>(ServiceProvider provider)
    {
        using (provider)
        using (var scope = provider.CreateScope())
        {
            return (scope.ServiceProvider.GetServices<TFirst>().Count(), scope.ServiceProvider.GetServices<TLast>().Count());
        }
    }
}