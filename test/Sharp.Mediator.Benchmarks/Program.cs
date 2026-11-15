using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Sharp.Mediator.Benchmarks.Fixtures;
using Sharp.Mediator.Benchmarks.Scenarios;
using Sharp.Mediator.ScaleFixtures;

namespace Sharp.Mediator.Benchmarks;

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

    private static async Task SmokeCheck()
    {
        var request = new RequestBenchmarks();
        request.Setup();
        Console.WriteLine(
            $"Sharp request: {await request.Sharp_Typed()} {await request.Sharp_Interface()} " +
            $"{await request.Sharp_Scoped_Typed()}");

        request.Cleanup();

        var voidRequest = new VoidRequestBenchmarks();
        voidRequest.Setup();
        await voidRequest.Sharp();
        await voidRequest.Sharp_Scoped();
        voidRequest.Cleanup();
        Console.WriteLine("Sharp void request: ok");

        var requestObject = new RequestObjectBenchmarks();
        requestObject.Setup();
        Console.WriteLine($"Sharp object: {await requestObject.Sharp()} {await requestObject.Sharp_Scoped()}");
        requestObject.Cleanup();

        var publish = new PublishBenchmarks();
        publish.Setup();
        await publish.Sharp();
        await publish.Sharp_Scoped();
        publish.Cleanup();
        Console.WriteLine("Sharp publish: ok");

        var middleware = new MiddlewareBenchmarks();
        middleware.Setup();
        Console.WriteLine($"Sharp middleware: {await middleware.Sharp()} {await middleware.Sharp_Scoped()}");
        middleware.Cleanup();

        var validation = new ValidationBenchmarks();
        Console.WriteLine(
            $"Sharp validation: {await validation.NoValidator()} {await validation.OnePassingValidator()} " +
            $"{await validation.MultiplePassingValidators()}");

        var requestScope = new RequestScopeBenchmarks { Dispatches = 8 };
        requestScope.Setup();
        await requestScope.Publish();
        Console.WriteLine($"Sharp request scope: {await requestScope.Sharp()} {await requestScope.Sharp_Distinct()}, publish ok");

        Console.Write("Sharp scope cache:");
        foreach (var everyMessageFirst in (bool[])[false, true])
        {
            var scopeCache = new ScopeCacheBenchmarks { EveryMessageFirst = everyMessageFirst, Dispatches = 32 };
            scopeCache.Setup();
            Console.Write($" {await scopeCache.RequestScope_First()} {await scopeCache.RequestScope_Last()}");
        }

        Console.WriteLine();

        var longLived = new LongLivedScopeBenchmarks();
        longLived.Setup();
        Console.WriteLine($"Sharp long-lived scope: {await longLived.First()} {await longLived.Last()}");
        longLived.Cleanup();

        var lifetime = new LifetimeBenchmarks();
        lifetime.Setup();
        Console.WriteLine(
            $"Sharp lifetime: {await lifetime.Singleton()} {await lifetime.Scoped()} {await lifetime.Transient()} " +
            $"{await lifetime.Singleton_TransientHandler()} {await lifetime.Scoped_SingletonHandler()}");

        // A process runs a module initializer once, so here the scenario only shows that it finds the assembly by name.
        new StartupBenchmarks().ModuleInitializer();
        Console.WriteLine("Sharp startup: ok");

        var scale = new ScaleBenchmarks();
        scale.Setup();
        Console.WriteLine(
            $"Sharp scale: {await scale.Typed_First()} {await scale.Typed_Last()} {await scale.Interface_First()} " +
            $"{await scale.Interface_Last()} {await scale.RequestScope_First()} {await scale.RequestScope_Last()}");

        var firstDispatch = new FirstDispatchBenchmarks();
        firstDispatch.Setup();
        Console.Write(
            $"Sharp first dispatch: {await firstDispatch.Typed_First()} {await firstDispatch.Typed_Last()} " +
            $"{await firstDispatch.Interface_Last()} {await firstDispatch.Object_Last()} ");

        firstDispatch.SetupAfterFirst();
        Console.WriteLine(await firstDispatch.Typed_Last_AfterFirst());

        var composition = new CompositionBenchmarks();
        Console.WriteLine(
            $"Sharp composition: {await composition.Handlers().Request<LastMessage, int>(new())} " +
            $"{await composition.Handlers_Middleware().Request<LastMessage, int>(new())}");

        var stream = new StreamRequestBenchmarks();
        stream.Setup();
        Console.WriteLine(
            $"Sharp stream: {await Count(stream.Sharp_Typed())} {await Count(stream.Sharp_Interface())} " +
            $"{await Count(stream.Sharp_Object())} {await Count(stream.Sharp_Scoped_Typed())} " +
            $"{await Count(stream.Sharp_Typed_Cancelable())}, first {await stream.Sharp_Typed_FirstItem()}, " +
            $"all {await stream.Sharp_Typed_AllItems()} {await stream.Sharp_Typed_Cancelable_AllItems()} " +
            $"{await stream.Sharp_Object_AllItems()}, early {await stream.Sharp_Typed_EarlyDispose()}, " +
            $"canceled {await stream.Sharp_Typed_Canceled()} {await stream.Sharp_Object_Canceled()}");

        stream.Cleanup();

        Console.Write("Sharp completion:");
        foreach (var completion in Enum.GetValues<Completion>())
        {
            var scenario = new CompletionBenchmarks { Completion = completion };
            scenario.Setup();
            Console.Write(
                $" {completion} {await scenario.Request_Typed()} {await scenario.Request_Object()} " +
                $"{await scenario.Publish_Sequential()} {await scenario.Publish_Parallel()}");
        }

        Console.WriteLine();

        Console.Write("Sharp event levels:");
        foreach (var publisher in Enum.GetValues<EventLevelBenchmarks.PublisherKind>())
        {
            var levels = new EventLevelBenchmarks { Publisher = publisher };
            levels.Setup();
            await levels.Own();
            await levels.OwnAndBase();
            await levels.OwnAndInterface();
            await levels.OwnAndInterface_ValueType();
            Console.Write($" {publisher} ok");
        }

        Console.WriteLine();

        var handlerCounts = new EventHandlerCountBenchmarks();
        handlerCounts.Setup();
        await handlerCounts.One();
        await handlerCounts.Two();
        await handlerCounts.Four();
        await handlerCounts.Six();
        handlerCounts.Mixed();
        var afterOne = new EventHandlerCountBenchmarks();
        afterOne.SetupAfterOne();
        await afterOne.TwoAfterOne();
        Console.WriteLine("Sharp event handler counts: ok");
    }

    private static async ValueTask<int> Count<T>(IAsyncEnumerable<T> items)
    {
        var count = 0;
        await foreach (var _ in items) count++;

        return count;
    }
}