using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator;
using Sharp.Mediator.LibraryFixtures;
using Sharp.Mediator.NoGeneratorFixtures;
using Sharp.Mediator.Responses;
using Sharp.Mediator.Validation;
using Sharp.Validator;
using Sharp.Validator.Result;

var services = new ServiceCollection();
services.AddMediator(b => b
    .AddHandler<PingHandler>()
    .AddHandler<NoopHandler>()
    .AddHandler<TickHandler>()
    .AddHandler<CountHandler>()
    .AddHandler<CreateThingHandler>()
    .AddRequestValidationMiddleware()
    .AddResponseValidationMiddleware());
services.AddSingleton<IValidator<CreateThing>>(new NameValidator());

var provider = services.BuildServiceProvider();
var mediator = provider.GetRequiredService<IMediator>();

var failures = 0;

void Check(bool ok, string label)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")}: {label}");
    if (!ok) failures++;
}

// Strongly-typed value response (value-type generic — an AOT specialization canary).
Check(await mediator.Request<Ping, int>(new Ping(42)) == 42, "Request<Ping,int>");

// Strongly-typed void request.
await mediator.Request(new Noop());
Check(true, "Request<Noop>");

// Interface-typed request by runtime type, value response.
Check(await mediator.Request(new Ping(7)) == 7, "Request<int>(IRequest<int>)");

// Object-typed request: value response (boxed), void (null), and unknown (ArgumentException).
Check(await mediator.Request((object)new Ping(5)) is 5, "Request(object) value");
Check(await mediator.Request((object)new Noop()) is null, "Request(object) void");
try
{
    await mediator.Request("not-a-request");
    Check(false, "Request(object) unknown throws");
}
catch (ArgumentException)
{
    Check(true, "Request(object) unknown throws");
}

// Events: typed and object-typed.
await mediator.Publish(new Tick());
await mediator.Publish((object)new Tick());
Check(true, "Publish typed + object");

// Streams with a value-type element (the key Native AOT canary).
var typed = new List<int>();
await foreach (var i in mediator.RequestStream<Count, int>(new Count(3))) typed.Add(i);
Check(typed.SequenceEqual([0, 1, 2]), "RequestStream<Count,int>");

var iface = new List<int>();
await foreach (var i in mediator.RequestStream(new Count(2))) iface.Add(i);
Check(iface.SequenceEqual([0, 1]), "RequestStream<int>(IStreamRequest<int>)");

var boxed = new List<object?>();
await foreach (var i in mediator.RequestStream((object)new Count(2))) boxed.Add(i);
Check(boxed.Count == 2, "RequestStream(object)");

// Validation middleware (cross-assembly open-generic closed by the generator over an IResponse, new() type).
var invalid = await mediator.Request<CreateThing, ThingResponse>(new CreateThing(""));
Check(invalid.Status == ResponseStatus.Invalid, "validation -> Invalid");
var valid = await mediator.Request<CreateThing, ThingResponse>(new CreateThing("widget"));
Check(valid is { Status: ResponseStatus.Success, Id: "widget" }, "validation -> Success");

// Stream validation closes over a value-type stream because the generator finds the package's public middleware.
var streamValidationServices = new ServiceCollection();
streamValidationServices.AddSingleton<IValidator<Count>>(new NonNegativeCountValidator());
streamValidationServices.AddMediator(b => b
    .AddHandler<CountHandler>()
    .AddStreamValidationMiddleware());
var streamValidated = streamValidationServices.BuildServiceProvider().GetRequiredService<IMediator>();
var validStream = new List<int>();
await foreach (var i in streamValidated.RequestStream<Count, int>(new Count(1))) validStream.Add(i);
Check(validStream.SequenceEqual([0]), "stream validation -> valid stream");
try
{
    await foreach (var _ in streamValidated.RequestStream<Count, int>(new Count(-1)))
    {
    }

    Check(false, "stream validation -> invalid stream");
}
catch (ValidationException)
{
    Check(true, "stream validation -> invalid stream");
}

var validationConflict = new ServiceCollection();
try
{
    validationConflict.AddMediator(b => b
        .AddValidationMiddleware()
        .AddResponseValidationMiddleware());

    Check(false, "validation typed-policy conflict");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("mutually exclusive", StringComparison.Ordinal),
        "validation typed-policy conflict");
}

// Cached fast-path mode (Singleton, auto-cached) — exercises the per-instance slot arrays + array publish loop under
// Native AOT, including the value-type generic canary.
var cachedServices = new ServiceCollection();
cachedServices.AddMediator(b => b
    .WithLifetime(ServiceLifetime.Singleton)
    .AddHandler<PingHandler>()
    .AddHandler<NoopHandler>()
    .AddHandler<TickHandler>()
    .AddHandler<CountHandler>()
    .AddHandler<CreateThingHandler>()
    .AddResponseValidationMiddleware());
cachedServices.AddSingleton<IValidator<CreateThing>>(new NameValidator());
var cached = cachedServices.BuildServiceProvider().GetRequiredService<IMediator>();

Check(await cached.Request<Ping, int>(new Ping(42)) == 42, "cached Request<Ping,int>");
Check(await cached.Request((object)new Ping(5)) is 5, "cached Request(object)");
Check(await cached.Request(new Ping(7)) == 7, "cached Request<int>(IRequest<int>)");
await cached.Request(new Noop());
Check(true, "cached Request<Noop>");
await cached.Publish(new Tick());
await cached.Publish((object)new Tick());
Check(true, "cached Publish typed + object");
var cachedInvalid = await cached.Request<CreateThing, ThingResponse>(new CreateThing(""));
Check(cachedInvalid.Status == ResponseStatus.Invalid, "cached validation -> Invalid");
var cachedValid = await cached.Request<CreateThing, ThingResponse>(new CreateThing("widget"));
Check(cachedValid is { Status: ResponseStatus.Success, Id: "widget" }, "cached validation -> Success");

// Scoped lifetime: caching is automatic and per-scope. Two scopes verify cache hit within a scope and isolation across
// scopes (each scope gets a fresh per-instance array). Exercises slots + arrays + Unsafe.As + MediatorBox under Native
// AOT with the most common real-world lifetime.
var scopedServices = new ServiceCollection();
scopedServices.AddMediator(b => b
    .WithLifetime(ServiceLifetime.Scoped)
    .AddHandler<PingHandler>()
    .AddHandler<NoopHandler>()
    .AddHandler<TickHandler>()
    .AddHandler<CountHandler>()
    .AddHandler<CreateThingHandler>()
    .AddResponseValidationMiddleware());
scopedServices.AddSingleton<IValidator<CreateThing>>(new NameValidator());
var scopedProvider = scopedServices.BuildServiceProvider();

using (var scope1 = scopedProvider.CreateScope())
{
    var m1 = scope1.ServiceProvider.GetRequiredService<IMediator>();
    Check(await m1.Request<Ping, int>(new Ping(11)) == 11, "scoped Request<Ping,int> (cold)");
    Check(await m1.Request<Ping, int>(new Ping(12)) == 12, "scoped Request<Ping,int> (warm hit)");
    Check(await m1.Request((object)new Ping(13)) is 13, "scoped Request(object)");
    await m1.Request(new Noop());
    await m1.Publish(new Tick());
    await m1.Publish((object)new Tick());
    Check(true, "scoped void + publish");
    var scopedInvalid = await m1.Request<CreateThing, ThingResponse>(new CreateThing(""));
    Check(scopedInvalid.Status == ResponseStatus.Invalid, "scoped validation -> Invalid");
}

using (var scope2 = scopedProvider.CreateScope())
{
    var m2 = scope2.ServiceProvider.GetRequiredService<IMediator>();
    Check(await m2.Request<Ping, int>(new Ping(21)) == 21, "scoped 2nd-scope Request (fresh array)");

    var requester = scope2.ServiceProvider.GetRequiredService<IRequester>();
    var publisher = scope2.ServiceProvider.GetRequiredService<IPublisher>();
    Check(ReferenceEquals(requester, m2) && ReferenceEquals(publisher, m2), "IRequester and IPublisher forward to IMediator");
    Check(await requester.Request<Ping, int>(new Ping(22)) == 22, "IRequester.Request<Ping,int>");
    await publisher.Publish(new Tick());
    Check(true, "IPublisher.Publish");
}

// AddHandlers(assembly) source-generated path: GeneratedHandlerRegistrar registers every handler in this assembly
// through HandlerRegistry — the AOT-safe replacement for reflection assembly scanning. Proves the registrar's
// [ModuleInitializer] ran and the closed-generic descriptors resolve under Native AOT.
var scannedServices = new ServiceCollection();
scannedServices.AddMediator(b => b.AddHandlers(typeof(Ping).Assembly));
var scanned = scannedServices.BuildServiceProvider().GetRequiredService<IMediator>();
Check(await scanned.Request<Ping, int>(new Ping(123)) == 123, "AddHandlers(assembly) SG path: Request<Ping,int>");
await scanned.Request(new Noop());
Check(true, "AddHandlers(assembly) SG path: Request<Noop>");
await scanned.Publish(new Tick());
Check(true, "AddHandlers(assembly) SG path: Publish<Tick>");
var scannedStream = new List<int>();
await foreach (var i in scanned.RequestStream<Count, int>(new Count(2))) scannedStream.Add(i);
Check(scannedStream.SequenceEqual([0, 1]), "AddHandlers(assembly) SG path: RequestStream<Count,int>");

// This assembly declares more request routes than one generated finder method checks. Compiled ahead of time, the split
// finder calls each of its methods in turn rather than read type names.
object[] routes =
[
    new AotRoute01(), new AotRoute02(), new AotRoute03(), new AotRoute04(), new AotRoute05(), new AotRoute06(),
    new AotRoute07(), new AotRoute08(), new AotRoute09(), new AotRoute10(), new AotRoute11(), new AotRoute12(),
    new AotRoute13(), new AotRoute14(), new AotRoute15(), new AotRoute16(), new AotRoute17()
];
var routed = 0;
for (var route = 0; route < routes.Length; route++)
    if (await scanned.Request(routes[route]) is int value && value == route + 1)
        routed++;
Check(routed == routes.Length, "AddHandlers(assembly) SG path: a split finder reaches every route");

// Multi-assembly canary using the same class-library fixtures as the integration tests. This covers the separate
// generated dispatchers, interface/object paths, stream paths, and the closures of middleware another assembly
// declares: a package's, which each assembly finds in its metadata, and the app's own, which the app's generator closes
// over the library's public messages, in the actual native executable.
var multiServices = new ServiceCollection();
multiServices.AddSingleton<LibraryTrace>();
multiServices.AddSingleton<PackagedTrace>();
multiServices.AddMediator(b => b
    .AddHandler<PingHandler>()
    .AddHandler<LibraryPingHandler>()
    .AddHandler<LibraryVoidHandler>()
    .AddHandler<LibraryEventHandler>()
    .AddHandler<LibraryCountHandler>()
    .AddPackagedMiddleware()
    .AddMiddleware(typeof(AotLocalMiddleware<,>)));
var multiProvider = multiServices.BuildServiceProvider();
var multi = multiProvider.GetRequiredService<IMediator>();

Check(await multi.Request<Ping, int>(new Ping(3)) == 3, "multi assembly typed app request");
Check(await multi.Request<LibraryPing, int>(new LibraryPing(3)) == 30, "multi assembly typed library request");
IRequest<int> libraryRequest = new LibraryPing(4);
Check(await multi.Request(libraryRequest) == 40, "multi assembly interface library request");
Check(await multi.Request((object)new Ping(5)) is 5, "multi assembly object app request");
Check(await multi.Request((object)new LibraryPing(5)) is 50, "multi assembly object library request");
Check(await multi.Request((object)new LibraryVoid()) is null, "multi assembly object library void");
await multi.Publish((object)new LibraryEvent("aot"));
Check(multiProvider.GetRequiredService<LibraryTrace>().Entries.Contains("library:aot"), "multi assembly object event");

var multiTypedStream = new List<int>();
await foreach (var i in multi.RequestStream<LibraryCount, int>(new LibraryCount(2))) multiTypedStream.Add(i);
Check(multiTypedStream.SequenceEqual([0, 1]), "multi assembly typed library stream");
IStreamRequest<int> libraryStreamRequest = new LibraryCount(2);
var multiInterfaceStream = new List<int>();
await foreach (var i in multi.RequestStream(libraryStreamRequest)) multiInterfaceStream.Add(i);
Check(multiInterfaceStream.SequenceEqual([0, 1]), "multi assembly interface library stream");
var multiObjectStream = new List<object?>();
await foreach (var i in multi.RequestStream((object)new LibraryCount(2))) multiObjectStream.Add(i);
Check(multiObjectStream.SequenceEqual([0, 1]), "multi assembly object library stream");

var packagedEntries = multiProvider.GetRequiredService<PackagedTrace>().Entries;
Check(packagedEntries.Contains("packaged:Ping"), "packaged middleware app closure");
Check(packagedEntries.Contains("packaged:LibraryPing"), "packaged middleware library closure");
var libraryEntries = multiProvider.GetRequiredService<LibraryTrace>().Entries;
Check(libraryEntries.Contains("local:Ping"), "app middleware app closure");
Check(libraryEntries.Contains("local:LibraryPing"), "app middleware library closure");

// Dispatch follows what a message is, whichever method it is sent through.
var shapes = new ServiceCollection()
    .AddMediator(b => b.AddHandler<AotShapeHandler>().AddHandler<AotSquareHandler>().WithLifetime(ServiceLifetime.Singleton))
    .BuildServiceProvider().GetRequiredService<IMediator>();
Check(await shapes.Request<AotShape, string>(new AotSquare()) == "square", "typed base call reaches the derived handler");
Check(await shapes.Request<AotShape, string>(new AotSquare()) == "square", "typed base call reaches the derived handler (warm)");
Check(await shapes.Request<AotShape, string>(new AotUnitSquare()) == "square", "undeclared type reaches its nearest declared base");
Check(await shapes.Request((object)new AotUnitSquare()) is "square", "object call reaches the same handler");

// An event reaches every declared type it is: its own, its base classes' and its interfaces'.
var levelTrace = new List<string>();
var levels = new ServiceCollection()
    .AddSingleton(levelTrace)
    .AddMediator(b => b
        .AddHandler<AotLevelDerivedHandler>()
        .AddHandler<AotLevelBaseHandler>()
        .AddHandler<AotLevelAuditedHandler>()
        .AddHandler<AotLevelPingHandler>()
        .WithLifetime(ServiceLifetime.Singleton))
    .BuildServiceProvider().GetRequiredService<IMediator>();
await levels.Publish<AotLevelBase>(new AotLevelDerived());
Check(levelTrace.SequenceEqual(["derived", "base", "audited"]), "event reaches its own, base and interface levels");
levelTrace.Clear();
await levels.Publish((object)new AotLevelPing());
Check(levelTrace.SequenceEqual(["ping", "audited"]), "value-type event reaches its interface level");

// This project treats warnings as errors through TreatWarningsAsErrors, which the compiler sees, and a response that
// meets a middleware's constraint only through variance still gets the middleware: the generator judges the pair as it
// compiles the generated code, so the nullable warnings of that judgment never become errors.
var varianceTrace = new LibraryTrace();
var variance = new ServiceCollection()
    .AddSingleton(varianceTrace)
    .AddMediator(b => b
        .AddHandler<AotNamesHandler>()
        .AddMiddleware(typeof(AotVarianceMiddleware<,>)))
    .BuildServiceProvider().GetRequiredService<IMediator>();
await variance.Request<AotNames, List<string>>(new AotNames());
Check(varianceTrace.Entries.SequenceEqual(["variance:AotNames"]), "middleware met through variance under TreatWarningsAsErrors");

// A Singleton mediator rejects a disposable Transient handler, which the root provider would keep for the life of the
// process: one registered by its type, and one whose factory declares its type, which is read from the type of the
// factory delegate. A factory that declares only the service type hides the implementation and is accepted.
foreach (var (how, register) in new (string, Action<IServiceCollection>)[]
         {
             ("by its type", static x => x.AddTransient<IRequestHandler<AotDisposable, int>, AotDisposableHandler>()), ("by a factory of its type",
                 static x =>
                     x.AddTransient<IRequestHandler<AotDisposable, int>, AotDisposableHandler>(static _ => new AotDisposableHandler()))
         })
{
    var rejected = new ServiceCollection();
    register(rejected);

    try
    {
        rejected.AddMediator(static b => b.WithLifetime(ServiceLifetime.Singleton));
        Check(false, $"disposable Transient handler registered {how} rejected under a Singleton mediator");
    }
    catch (InvalidOperationException exception)
    {
        Check(
            exception.Message.Contains(nameof(AotDisposableHandler), StringComparison.Ordinal),
            $"disposable Transient handler registered {how} rejected under a Singleton mediator");
    }
}

var serviceTyped = new ServiceCollection();
serviceTyped.AddTransient<IRequestHandler<AotDisposable, int>>(static _ => new AotDisposableHandler());
serviceTyped.AddMediator(static b => b.WithLifetime(ServiceLifetime.Singleton));
Check(
    await serviceTyped.BuildServiceProvider().GetRequiredService<IMediator>().Request(new AotDisposable()) == 1,
    "disposable Transient handler behind a factory of the service type accepted");

// An event handler registered directly from an assembly built without the generator is rejected when nothing declares a
// handler for its event, which a publish would never reach, and accepted for an event something declares, where it runs
// with that event's handlers. The check asks for the assembly's registrar rather than reflecting over it.
try
{
    var undeclared = new ServiceCollection();
    undeclared.AddScoped<IEventHandler<NoGeneratorEvent>, NoGeneratorEventHandler>();
    undeclared.AddMediator(static b => b.AddHandler<TickHandler>());
    Check(false, "directly registered handler of an undeclared event rejected");
}
catch (InvalidOperationException exception)
{
    Check(
        exception.Message.Contains(nameof(NoGeneratorEventHandler), StringComparison.Ordinal),
        "directly registered handler of an undeclared event rejected");
}

var packagedEvents = new ServiceCollection();
packagedEvents.AddSingleton<PackagedTrace>();
packagedEvents.AddScoped<IEventHandler<Tick>, PackagedEventHandler<Tick>>();
packagedEvents.AddMediator(static b => b.AddHandler<TickHandler>());

var packagedEventProvider = packagedEvents.BuildServiceProvider();
await packagedEventProvider.CreateScope().ServiceProvider.GetRequiredService<IMediator>().Publish(new Tick());

Check(
    packagedEventProvider.GetRequiredService<PackagedTrace>().Entries.SequenceEqual(["packaged-event:Tick"]),
    "directly registered handler from an assembly without the generator runs for a declared event");

if (!RuntimeFeature.IsDynamicCodeSupported)
{
    // A handler assembly without the bundled generator cannot be reflected over under Native AOT.
    try
    {
        new ServiceCollection().AddMediator(b => b.AddHandlers(typeof(NoGeneratorRequest).Assembly));
        Check(false, "generatorless AddHandlers guidance");
    }
    catch (InvalidOperationException exception)
    {
        Check(
            exception.Message.Contains("No source-generated handler registrations", StringComparison.Ordinal) &&
            exception.Message.Contains("analyzer", StringComparison.Ordinal),
            "generatorless AddHandlers guidance");
    }

    // The library's generator cannot see this app-declared middleware, and only the library can name its internal
    // stream, so no generated code closes that pair. A container that does not register the internal stream's handler
    // never needs the pair, so startup succeeds and the public stream gets the middleware the application closed.
    var gapTrace = new LibraryTrace();
    var unusedGap = new ServiceCollection()
        .AddSingleton(gapTrace)
        .AddMediator(b => b
            .AddHandler<LibraryCountHandler>()
            .AddMiddleware(typeof(AotLocalStreamMiddleware<,>)))
        .BuildServiceProvider().GetRequiredService<IMediator>();

    var gapItems = new List<int>();
    await foreach (var item in unusedGap.RequestStream(new LibraryCount(2))) gapItems.Add(item);

    Check(
        gapItems.SequenceEqual([0, 1]) && gapTrace.Entries.SequenceEqual(["local-stream:LibraryCount"]),
        "cross-assembly middleware gap skipped for an unregistered handler");

    // Registering that handler needs the pair, and AOT must fail with guidance instead of attempting reflection.
    try
    {
        new ServiceCollection().AddMediator(b => b
            .AddHandlers(typeof(LibraryCount).Assembly)
            .AddMiddleware(typeof(AotLocalStreamMiddleware<,>)));

        Check(false, "cross-assembly middleware gap guidance");
    }
    catch (InvalidOperationException exception)
    {
        Check(
            exception.Message.Contains("InternalLibraryCount", StringComparison.Ordinal) &&
            exception.Message.Contains(
                "in place of typeof(AotLocalStreamMiddleware<,>)", StringComparison.Ordinal),
            "cross-assembly middleware gap guidance");
    }
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)");

return failures;

internal readonly record struct Ping(int Value) : IRequest<int>;

internal sealed class PingHandler : IRequestHandler<Ping, int>
{
    public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(message.Value);
}

internal sealed record Noop : IRequest;

internal sealed record AotDisposable : IRequest<int>;

internal sealed class AotDisposableHandler : IRequestHandler<AotDisposable, int>, IDisposable
{
    public void Dispose()
    {
    }

    public ValueTask<int> Handle(AotDisposable message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
}

internal sealed class NoopHandler : IRequestHandler<Noop>
{
    public ValueTask Handle(Noop message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed record Tick : IEvent;

internal sealed class TickHandler : IEventHandler<Tick>
{
    public ValueTask Handle(Tick message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed record AotRoute01 : IRequest<int>;

internal sealed record AotRoute02 : IRequest<int>;

internal sealed record AotRoute03 : IRequest<int>;

internal sealed record AotRoute04 : IRequest<int>;

internal sealed record AotRoute05 : IRequest<int>;

internal sealed record AotRoute06 : IRequest<int>;

internal sealed record AotRoute07 : IRequest<int>;

internal sealed record AotRoute08 : IRequest<int>;

internal sealed record AotRoute09 : IRequest<int>;

internal sealed record AotRoute10 : IRequest<int>;

internal sealed record AotRoute11 : IRequest<int>;

internal sealed record AotRoute12 : IRequest<int>;

internal sealed record AotRoute13 : IRequest<int>;

internal sealed record AotRoute14 : IRequest<int>;

internal sealed record AotRoute15 : IRequest<int>;

internal sealed record AotRoute16 : IRequest<int>;

internal sealed record AotRoute17 : IRequest<int>;

internal sealed class AotRouteHandler :
    IRequestHandler<AotRoute01, int>, IRequestHandler<AotRoute02, int>, IRequestHandler<AotRoute03, int>,
    IRequestHandler<AotRoute04, int>, IRequestHandler<AotRoute05, int>, IRequestHandler<AotRoute06, int>,
    IRequestHandler<AotRoute07, int>, IRequestHandler<AotRoute08, int>, IRequestHandler<AotRoute09, int>,
    IRequestHandler<AotRoute10, int>, IRequestHandler<AotRoute11, int>, IRequestHandler<AotRoute12, int>,
    IRequestHandler<AotRoute13, int>, IRequestHandler<AotRoute14, int>, IRequestHandler<AotRoute15, int>,
    IRequestHandler<AotRoute16, int>, IRequestHandler<AotRoute17, int>
{
    public ValueTask<int> Handle(AotRoute01 message, CancellationToken cancellationToken) => ValueTask.FromResult(1);

    public ValueTask<int> Handle(AotRoute02 message, CancellationToken cancellationToken) => ValueTask.FromResult(2);

    public ValueTask<int> Handle(AotRoute03 message, CancellationToken cancellationToken) => ValueTask.FromResult(3);

    public ValueTask<int> Handle(AotRoute04 message, CancellationToken cancellationToken) => ValueTask.FromResult(4);

    public ValueTask<int> Handle(AotRoute05 message, CancellationToken cancellationToken) => ValueTask.FromResult(5);

    public ValueTask<int> Handle(AotRoute06 message, CancellationToken cancellationToken) => ValueTask.FromResult(6);

    public ValueTask<int> Handle(AotRoute07 message, CancellationToken cancellationToken) => ValueTask.FromResult(7);

    public ValueTask<int> Handle(AotRoute08 message, CancellationToken cancellationToken) => ValueTask.FromResult(8);

    public ValueTask<int> Handle(AotRoute09 message, CancellationToken cancellationToken) => ValueTask.FromResult(9);

    public ValueTask<int> Handle(AotRoute10 message, CancellationToken cancellationToken) => ValueTask.FromResult(10);

    public ValueTask<int> Handle(AotRoute11 message, CancellationToken cancellationToken) => ValueTask.FromResult(11);

    public ValueTask<int> Handle(AotRoute12 message, CancellationToken cancellationToken) => ValueTask.FromResult(12);

    public ValueTask<int> Handle(AotRoute13 message, CancellationToken cancellationToken) => ValueTask.FromResult(13);

    public ValueTask<int> Handle(AotRoute14 message, CancellationToken cancellationToken) => ValueTask.FromResult(14);

    public ValueTask<int> Handle(AotRoute15 message, CancellationToken cancellationToken) => ValueTask.FromResult(15);

    public ValueTask<int> Handle(AotRoute16 message, CancellationToken cancellationToken) => ValueTask.FromResult(16);

    public ValueTask<int> Handle(AotRoute17 message, CancellationToken cancellationToken) => ValueTask.FromResult(17);
}

internal record AotShape : IRequest<string>;

internal record AotSquare : AotShape;

internal sealed record AotUnitSquare : AotSquare;

internal sealed class AotShapeHandler : IRequestHandler<AotShape, string>
{
    public ValueTask<string> Handle(AotShape message, CancellationToken cancellationToken) => new("shape");
}

internal sealed class AotSquareHandler : IRequestHandler<AotSquare, string>
{
    public ValueTask<string> Handle(AotSquare message, CancellationToken cancellationToken) => new("square");
}

internal interface IAotAudited : IEvent;

internal record AotLevelBase : IEvent;

internal sealed record AotLevelDerived : AotLevelBase, IAotAudited;

internal readonly record struct AotLevelPing : IAotAudited;

internal sealed class AotLevelBaseHandler(List<string> trace) : IEventHandler<AotLevelBase>
{
    public ValueTask Handle(AotLevelBase message, CancellationToken cancellationToken)
    {
        trace.Add("base");
        return default;
    }
}

internal sealed class AotLevelDerivedHandler(List<string> trace) : IEventHandler<AotLevelDerived>
{
    public ValueTask Handle(AotLevelDerived message, CancellationToken cancellationToken)
    {
        trace.Add("derived");
        return default;
    }
}

internal sealed class AotLevelAuditedHandler(List<string> trace) : IEventHandler<IAotAudited>
{
    public ValueTask Handle(IAotAudited message, CancellationToken cancellationToken)
    {
        trace.Add("audited");
        return default;
    }
}

internal sealed class AotLevelPingHandler(List<string> trace) : IEventHandler<AotLevelPing>
{
    public ValueTask Handle(AotLevelPing message, CancellationToken cancellationToken)
    {
        trace.Add("ping");
        return default;
    }
}

internal readonly record struct Count(int N) : IStreamRequest<int>;

internal sealed class CountHandler : IStreamRequestHandler<Count, int>
{
    public async IAsyncEnumerable<int> Handle(Count request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.N; i++)
        {
            yield return i;
            await Task.Yield();
        }
    }
}

internal sealed record CreateThing(string Name) : IRequest<ThingResponse>;

internal sealed class ThingResponse : Response
{
    public string? Id { get; init; }
}

internal sealed class CreateThingHandler : IRequestHandler<CreateThing, ThingResponse>
{
    public ValueTask<ThingResponse> Handle(CreateThing message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ThingResponse { Status = ResponseStatus.Success, Id = message.Name });
}

internal sealed class AotLocalMiddleware<TMessage, TResponse>(LibraryTrace trace) : IMiddleware<TMessage, TResponse>
    where TMessage : IMessage<TResponse>
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandle<TMessage, TResponse> next,
        CancellationToken cancellationToken
    )
    {
        trace.Add($"local:{typeof(TMessage).Name}");
        return next(message, cancellationToken);
    }
}

internal sealed record AotNames : IRequest<List<string>>;

internal sealed class AotNamesHandler : IRequestHandler<AotNames, List<string>>
{
    public ValueTask<List<string>> Handle(AotNames message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new List<string>());
}

// List<string> meets IEnumerable<object> only through IEnumerable<out T>.
internal sealed class AotVarianceMiddleware<TMessage, TResponse>(LibraryTrace trace) : IMiddleware<TMessage, TResponse>
    where TMessage : IMessage<TResponse>
    where TResponse : IEnumerable<object>
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandle<TMessage, TResponse> next,
        CancellationToken cancellationToken
    )
    {
        trace.Add($"variance:{typeof(TMessage).Name}");
        return next(message, cancellationToken);
    }
}

internal sealed class AotLocalStreamMiddleware<TRequest, TResponse>(LibraryTrace trace) : IStreamMiddleware<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    public IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamMessageHandle<TRequest, TResponse> next,
        CancellationToken cancellationToken
    )
    {
        trace.Add($"local-stream:{typeof(TRequest).Name}");
        return next(request, cancellationToken);
    }
}

internal sealed class NonNegativeCountValidator : IValidator<Count>
{
    public ValidationResult Validate(object model) => Run((Count)model);

    public ValidationResult Validate(ValidationContext<Count> context) => Run(context.Model);

    public ValueTask<ValidationResult> ValidateAsync(ValidationContext<Count> context, CancellationToken cancellationToken = default) => new(Run(context.Model));

    public ValidationResult Validate(Count model, Action<ValidationContext<Count>>? configure = null) => Run(model);

    public ValueTask<ValidationResult> ValidateAsync(object model, CancellationToken cancellationToken = default) => new(Run((Count)model));

    public ValueTask<ValidationResult> ValidateAsync(Count model, CancellationToken cancellationToken = default) => new(Run(model));

    public ValueTask<ValidationResult> ValidateAsync(
        Count model,
        Action<ValidationContext<Count>>? configure,
        CancellationToken cancellationToken = default
    ) => new(Run(model));

    private static ValidationResult Run(Count model) =>
        model.N < 0
            ? new ValidationResult([new ValidationError("N", "negative", "NEGATIVE")])
            : new ValidationResult([]);
}

internal sealed class NameValidator : IValidator<CreateThing>
{
    public ValidationResult Validate(object model) => Run((CreateThing)model);

    public ValidationResult Validate(ValidationContext<CreateThing> context) => Run(context.Model);

    public ValueTask<ValidationResult> ValidateAsync(ValidationContext<CreateThing> context, CancellationToken cancellationToken = default) => new(Run(context.Model));

    public ValidationResult Validate(CreateThing model, Action<ValidationContext<CreateThing>>? configure = null) => Run(model);

    public ValueTask<ValidationResult> ValidateAsync(object model, CancellationToken cancellationToken = default) => new(Run((CreateThing)model));

    public ValueTask<ValidationResult> ValidateAsync(CreateThing model, CancellationToken cancellationToken = default) => new(Run(model));

    public ValueTask<ValidationResult> ValidateAsync(
        CreateThing model,
        Action<ValidationContext<CreateThing>>? configure,
        CancellationToken cancellationToken = default
    ) => new(Run(model));

    private static ValidationResult Run(CreateThing model) =>
        string.IsNullOrEmpty(model.Name)
            ? new ValidationResult([new ValidationError("Name", "required", "REQUIRED")])
            : new ValidationResult([]);
}