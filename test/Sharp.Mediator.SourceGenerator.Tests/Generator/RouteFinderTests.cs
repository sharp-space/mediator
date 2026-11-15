using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using static Sharp.Mediator.SourceGenerator.Tests.Fixtures.GeneratorTestHarness;

namespace Sharp.Mediator.SourceGenerator.Tests.Generator;

// The generated route finders. Past sixteen routes a finder picks a method by a hash of the runtime type's name, so a
// first dispatch compiles the routes of one bucket rather than every route the assembly declares.
public sealed class RouteFinderTests
{
    private const int Requests = 30;
    private const int VoidRequests = 5;
    private const int Events = 20;
    private const int Streams = 17;

    private static readonly Lazy<Assembly> ManyRoutes =
        new(static () => CompileGeneratedToAssembly("ManyRoutes", ManyRoutesSource()));

    [Theory]
    [InlineData(16, 0)]
    [InlineData(17, 2)]
    [InlineData(33, 3)]
    public void RouteFinder_RouteCount_SplitsPastSixteenRoutes(int count, int buckets)
    {
        var source = new StringBuilder(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            """);

        for (var i = 0; i < count; i++)
            source.AppendLine(
                $$"""
                  public sealed class Ping{{i:D2}} : IRequest<int>;

                  public sealed class Ping{{i:D2}}Handler : IRequestHandler<Ping{{i:D2}}, int>
                  {
                      public ValueTask<int> Handle(Ping{{i:D2}} message, CancellationToken cancellationToken) => ValueTask.FromResult({{i}});
                  }
                  """);

        var run = RunGenerator(source.ToString());
        AssertCompiles(run);

        var text = RegistrationsText(run);
        var split = buckets > 0 ? 1 : 0;
        Assert.Equal(split, CountOccurrences(text, "private static uint HashName(string name)"));
        Assert.Equal(split, CountOccurrences(text, $"switch (HashName(messageType.Name) % {buckets}u)"));

        Assert.Equal(split, CountOccurrences(text,
            "if (!global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return FindRequestRouteInTurn(messageType);"));

        for (var i = 0; i < count; i++)
            Assert.Equal(1, CountOccurrences(text, $"if (messageType == typeof(global::GeneratorFixture.Ping{i:D2})) return"));
    }

    [Fact]
    public void RouteFinder_EveryKindSplit_SharesOneHashName()
    {
        var run = RunGenerator(ManyRoutesSource(), assemblyName: "ManyRoutes");
        AssertCompiles(run);

        var text = RegistrationsText(run);
        Assert.Equal(1, CountOccurrences(text, "private static uint HashName(string name)"));
        Assert.Equal(1, CountOccurrences(text, "switch (HashName(messageType.Name) % 3u)"));
        Assert.Equal(2, CountOccurrences(text, "switch (HashName(messageType.Name) % 2u)"));
    }

    [Fact]
    public async Task RouteFinder_ManyRoutes_DispatchesEachRequestToItsHandler()
    {
        await using var provider = Provider(ManyRoutes.Value);
        var mediator = provider.GetRequiredService<IMediator>();
        for (var i = 0; i < Requests; i++)
            Assert.Equal(i, await mediator.Request(Create($"ManyRoutes.Ping{i:D2}")));

        for (var i = 0; i < VoidRequests; i++)
        {
            Assert.Null(await mediator.Request(Create($"ManyRoutes.Noop{i}")));
            Assert.Equal(1, Hits($"ManyRoutes.Noop{i}"));
        }

        Assert.Equal(1001, await mediator.Request(Create("ManyRoutes.Outer+Nested")));
        Assert.Equal(1002, await mediator.Request(Create("ManyRoutes.Wrapper`1", typeof(int))));
        Assert.Equal(1003, await mediator.Request(Create("ManyRoutes.Wrapper`1", typeof(string))));
        Assert.Equal(1004, await mediator.Request(Create("ManyRoutes.First.Twin")));
        Assert.Equal(1005, await mediator.Request(Create("ManyRoutes.Second.Twin")));
        Assert.Equal(1006, await mediator.Request(Create("ManyRoutes.Αίτημα")));
        Assert.Equal(1007, await mediator.Request(Create("ManyRoutes.Shelf`1+Inner", typeof(int))));
        Assert.Equal(1008, await mediator.Request(Create("ManyRoutes.Shelf`1+Cell`1", typeof(int), typeof(string))));
        Assert.Equal(1009, await mediator.Request(Create("ManyRoutes.Point")));
    }

    [Fact]
    // Code compiled ahead of time calls the bucket methods in turn, as the Native AOT canary does when it runs. Here
    // the method is called directly, for every type of the assembly and the closed types of its generic messages.
    public void RouteFinder_InTurn_FindsTheRoutesTheHashFinds()
    {
        var assembly = ManyRoutes.Value;
        var registrar = assembly.GetTypes().Single(static x => x.Name == "GeneratedMediatorRegistrar");
        Type[] closed =
        [
            assembly.GetType("ManyRoutes.Wrapper`1")!.MakeGenericType(typeof(int)),
            assembly.GetType("ManyRoutes.Wrapper`1")!.MakeGenericType(typeof(string)),
            assembly.GetType("ManyRoutes.Shelf`1+Inner")!.MakeGenericType(typeof(int)),
            assembly.GetType("ManyRoutes.Shelf`1+Cell`1")!.MakeGenericType(typeof(int), typeof(string)),
            typeof(object)
        ];

        var types = assembly.GetTypes().Where(static x => !x.IsGenericTypeDefinition).Concat(closed).ToList();
        foreach (var (kind, routes) in (ReadOnlySpan<(string, int)>)[("Request", 44), ("Event", Events + 1), ("Stream", Streams)])
        {
            var hashed = Finder(registrar, $"Find{kind}Route");
            var inTurn = Finder(registrar, $"Find{kind}RouteInTurn");
            Assert.All(types, type => Assert.Equal(hashed(type)?.GetType(), inTurn(type)?.GetType()));
            Assert.Equal(routes, types.Count(type => inTurn(type) is not null));
        }
    }

    [Fact]
    // The walk asks for the derived type first, whose name hashes to a bucket without it, then for its base.
    public async Task RouteFinder_ManyRoutes_DispatchesADerivedRequestToItsBaseHandler()
    {
        await using var provider = Provider(ManyRoutes.Value);
        var mediator = provider.GetRequiredService<IMediator>();
        Assert.Equal(7, await mediator.Request(Create("ManyRoutes.DerivedPing")));
    }

    [Fact]
    public async Task RouteFinder_ManyRoutes_RejectsARequestWithoutAHandler()
    {
        await using var provider = Provider(ManyRoutes.Value);
        var mediator = provider.GetRequiredService<IMediator>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await mediator.Request(Create("ManyRoutes.Orphan")));
    }

    [Fact]
    // Each event also reaches the handler of an interface it is, whose route the interface finder looks up by name too.
    public async Task RouteFinder_ManyRoutes_PublishesEachEventToItsHandlers()
    {
        await using var provider = Provider(ManyRoutes.Value);
        var mediator = provider.GetRequiredService<IMediator>();
        for (var i = 0; i < Events; i++)
        {
            await mediator.Publish(Create($"ManyRoutes.Signal{i:D2}"));

            Assert.Equal(1, Hits($"ManyRoutes.Signal{i:D2}"));
        }

        Assert.Equal(Events, Hits("ManyRoutes.Audit"));
    }

    [Fact]
    public async Task RouteFinder_ManyRoutes_StreamsEachRequestFromItsHandler()
    {
        await using var provider = Provider(ManyRoutes.Value);
        var mediator = provider.GetRequiredService<IMediator>();
        for (var i = 0; i < Streams; i++)
        {
            var items = new List<object?>();
            await foreach (var item in mediator.RequestStream(Create($"ManyRoutes.Count{i:D2}"))) items.Add(item);

            Assert.Equal(i, Assert.Single(items));
        }
    }

    [Fact]
    // Each assembly's finder is asked for the other's types as well, and a name both declare hashes to a
    // bucket in each.
    public async Task RouteFinder_NameInTwoAssemblies_DispatchesToTheDeclaringAssembly()
    {
        var source = new StringBuilder(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace ManyRoutesElsewhere;

            public sealed class Twin : IRequest<int>;

            public sealed class TwinHandler : IRequestHandler<Twin, int>
            {
                public ValueTask<int> Handle(Twin message, CancellationToken cancellationToken) => ValueTask.FromResult(2001);
            }

            """);

        for (var i = 0; i < 16; i++)
            source.AppendLine(
                $$"""
                  public sealed class Ping{{i:D2}} : IRequest<int>;

                  public sealed class Ping{{i:D2}}Handler : IRequestHandler<Ping{{i:D2}}, int>
                  {
                      public ValueTask<int> Handle(Ping{{i:D2}} message, CancellationToken cancellationToken) => ValueTask.FromResult({{i}});
                  }
                  """);

        var elsewhere = CompileGeneratedToAssembly("ManyRoutesElsewhere", source.ToString());
        await using var provider = Provider(ManyRoutes.Value, elsewhere);
        var mediator = provider.GetRequiredService<IMediator>();
        Assert.Equal(1004, await mediator.Request(Create("ManyRoutes.First.Twin")));
        Assert.Equal(2001, await mediator.Request(Activator.CreateInstance(elsewhere.GetType("ManyRoutesElsewhere.Twin")!)!));
    }

    private static ServiceProvider Provider(params Assembly[] assemblies)
    {
        var services = new ServiceCollection();
        services.AddMediator(builder =>
        {
            foreach (var assembly in assemblies)
                builder.AddHandlers(assembly);
        });

        return services.BuildServiceProvider();
    }

    private static object Create(string name, params Type[] typeArguments)
    {
        var type = ManyRoutes.Value.GetType(name, true)!;
        return Activator.CreateInstance(typeArguments.Length == 0 ? type : type.MakeGenericType(typeArguments))!;
    }

    private static Func<Type, object?> Finder(Type registrar, string name) =>
        registrar.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<Type, object?>>();

    private static int Hits(string name) =>
        (int)ManyRoutes.Value.GetType(name, true)!.GetField("Hits")!.GetValue(null)!;

    // Every kind has more routes than one finder method checks. The named requests are types whose runtime names differ
    // from how C# writes them, or that share a name: a nested type, two closed types of one generic type, types nested
    // in a generic type, two types of one name in different namespaces, a name outside ASCII, and a struct. Counting
    // type parameters wrongly changes three of the names, and each changed name falls in another of the three request
    // buckets: Wrapper`1 written as Wrapper, Inner as Inner`1, and Cell`1 as Cell or Cell`2.
    private static string ManyRoutesSource()
    {
        var source = new StringBuilder(
            """
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;

            namespace ManyRoutes.First
            {
                public sealed class Twin : IRequest<int>;

                public sealed class TwinHandler : IRequestHandler<Twin, int>
                {
                    public ValueTask<int> Handle(Twin message, CancellationToken cancellationToken) => ValueTask.FromResult(1004);
                }
            }

            namespace ManyRoutes.Second
            {
                public sealed class Twin : IRequest<int>;

                public sealed class TwinHandler : IRequestHandler<Twin, int>
                {
                    public ValueTask<int> Handle(Twin message, CancellationToken cancellationToken) => ValueTask.FromResult(1005);
                }
            }

            namespace ManyRoutes
            {
                public static class Outer
                {
                    public sealed class Nested : IRequest<int>;
                }

                public sealed class NestedHandler : IRequestHandler<Outer.Nested, int>
                {
                    public ValueTask<int> Handle(Outer.Nested message, CancellationToken cancellationToken) => ValueTask.FromResult(1001);
                }

                public sealed class Wrapper<T> : IRequest<int>;

                public sealed class IntWrapperHandler : IRequestHandler<Wrapper<int>, int>
                {
                    public ValueTask<int> Handle(Wrapper<int> message, CancellationToken cancellationToken) => ValueTask.FromResult(1002);
                }

                public sealed class StringWrapperHandler : IRequestHandler<Wrapper<string>, int>
                {
                    public ValueTask<int> Handle(Wrapper<string> message, CancellationToken cancellationToken) => ValueTask.FromResult(1003);
                }

                public sealed class Αίτημα : IRequest<int>;

                public sealed class ΑίτημαHandler : IRequestHandler<Αίτημα, int>
                {
                    public ValueTask<int> Handle(Αίτημα message, CancellationToken cancellationToken) => ValueTask.FromResult(1006);
                }

                public static class Shelf<T>
                {
                    public sealed class Inner : IRequest<int>;

                    public sealed class Cell<U> : IRequest<int>;
                }

                public sealed class ShelfInnerHandler : IRequestHandler<Shelf<int>.Inner, int>
                {
                    public ValueTask<int> Handle(Shelf<int>.Inner message, CancellationToken cancellationToken) => ValueTask.FromResult(1007);
                }

                public sealed class ShelfCellHandler : IRequestHandler<Shelf<int>.Cell<string>, int>
                {
                    public ValueTask<int> Handle(Shelf<int>.Cell<string> message, CancellationToken cancellationToken) => ValueTask.FromResult(1008);
                }

                public readonly struct Point : IRequest<int>;

                public sealed class PointHandler : IRequestHandler<Point, int>
                {
                    public ValueTask<int> Handle(Point message, CancellationToken cancellationToken) => ValueTask.FromResult(1009);
                }

                public sealed class DerivedPing : Ping07;

                public sealed class Orphan : IRequest<int>;

                public interface IAudited : IEvent;

                public static class Audit
                {
                    public static int Hits;
                }

                public sealed class AuditedHandler : IEventHandler<IAudited>
                {
                    public ValueTask Handle(IAudited message, CancellationToken cancellationToken)
                    {
                        Audit.Hits++;
                        return default;
                    }
                }

            """);

        for (var i = 0; i < Requests; i++)
            source.AppendLine(
                $$"""
                  public class Ping{{i:D2}} : IRequest<int>;

                  public sealed class Ping{{i:D2}}Handler : IRequestHandler<Ping{{i:D2}}, int>
                  {
                      public ValueTask<int> Handle(Ping{{i:D2}} message, CancellationToken cancellationToken) => ValueTask.FromResult({{i}});
                  }
                  """);

        for (var i = 0; i < VoidRequests; i++)
            source.AppendLine(
                $$"""
                  public sealed class Noop{{i}} : IRequest
                  {
                      public static int Hits;
                  }

                  public sealed class Noop{{i}}Handler : IRequestHandler<Noop{{i}}>
                  {
                      public ValueTask Handle(Noop{{i}} message, CancellationToken cancellationToken)
                      {
                          Noop{{i}}.Hits++;
                          return default;
                      }
                  }
                  """);

        for (var i = 0; i < Events; i++)
            source.AppendLine(
                $$"""
                  public sealed class Signal{{i:D2}} : IAudited
                  {
                      public static int Hits;
                  }

                  public sealed class Signal{{i:D2}}Handler : IEventHandler<Signal{{i:D2}}>
                  {
                      public ValueTask Handle(Signal{{i:D2}} message, CancellationToken cancellationToken)
                      {
                          Signal{{i:D2}}.Hits++;
                          return default;
                      }
                  }
                  """);

        for (var i = 0; i < Streams; i++)
            source.AppendLine(
                $$"""
                  public sealed class Count{{i:D2}} : IStreamRequest<int>;

                  public sealed class Count{{i:D2}}Handler : IStreamRequestHandler<Count{{i:D2}}, int>
                  {
                      public async IAsyncEnumerable<int> Handle(Count{{i:D2}} message, [EnumeratorCancellation] CancellationToken cancellationToken)
                      {
                          await Task.CompletedTask;
                          yield return {{i}};
                      }
                  }
                  """);

        source.AppendLine("}");
        return source.ToString();
    }
}