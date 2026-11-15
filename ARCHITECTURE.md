# Architecture

This document describes the high-level architecture of Sharp.Mediator, for anyone about to change the code.
[README.md](README.md) describes how to use the library. Files and types are named rather than linked, so use symbol
search to find them.

## Overview

Sharp.Mediator sends a request to its handler, or an event to every handler of every type it is, through dependency
injection, with middleware around each handler. Finding handlers by scanning assemblies and closing generic types at
run time needs reflection that Native AOT and trimming do not allow, so Sharp.Mediator does that work in the build. A
source generator runs in every C# project that references the `Sharp.Mediator` package. It finds the handlers and
middleware there and emits closed-generic registrations, a route for each message type, and interceptors for the
mediator calls whose message type it can see.

At run time the generated code and the library meet in the `Sharp.Mediator.Bindings` namespace. Each
assembly's module initializer hands its registrations and routes to process-wide tables there. `AddMediator` turns
them into service registrations, and `DefaultMediator` dispatches each message by its runtime type, caching the
resolved middleware chain of every message type it dispatches.

## Code map

### `Sharp.Mediator`

The library that applications reference:

- Messages and handlers: `IRequest`, `IRequest<TResponse>`, `IEvent`, `IStreamRequest<TResponse>`, and their handler
  interfaces, such as `IRequestHandler<TRequest, TResponse>`.
- Middleware: `IMiddleware<TMessage>`, `IMiddleware<TMessage, TResponse>`, `IStreamMiddleware<TRequest, TResponse>`,
  and `MessageHandleBuilder`, which wraps a handler in its middleware. `StreamCancellation` composes a stream
  request's chain and passes the call's token to the enumerator of the handler's stream.
- The mediator: `IMediator`, which is `IRequester` plus `IPublisher`, implemented by the internal `DefaultMediator`,
  whose chain cache keeps the chains of high slots in `SparseChains` while it caches few.
- Registration: `MediatorServiceCollectionExtensions.AddMediator` and `MediatorBuilder`, split by concern into
  `MediatorBuilder.cs`, `MediatorBuilder.Handlers.cs`, and `MediatorBuilder.Middlewares.cs`. `ServiceRetention` reads
  the registrations for what a mediator may keep and, under Microsoft's container, which kinds of middleware it looks
  up, and says from which miss a mediator of its lifetime caches.
- Publishing: `IEventPublisher`, `EventHandlerExecutor`, and `Publishers/` with `SequentialPublisher` and
  `ParallelPublisher`, in the `Sharp.Mediator.Publishers` namespace.

`Bindings/` holds what generated code calls and the process-wide state behind dispatch:

- `MediatorRuntime`, `HandlerRegistry`, `MediatorFactory`, and `MediatorCalls`: public, and called only by generated
  code.
- `MessageRoutes`: dispatch by runtime type, from the current `Routing`: the resolvers of every assembly initialized
  so far, and what dispatch has learned from them. That is the route of each declared type, the route each runtime
  type walked to, and the levels of each event, with a cache slot for the executors of an event of several levels.
- `MessageSlots.cs`: a cache-slot index per message type, such as `RequestSlot<TRequest, TResponse>`, taken on first
  use and never 0, beside what dispatch has learned about the type: that a request or stream request type declares a
  handler, or that an event of exactly that type reaches only its own level.
- `EventEntry` and `MediatorBox`: one cached event level, and the adapters for the `object`-typed calls.

The package also carries the generator, in `analyzers/dotnet/cs`, and the build asset that turns the interceptors on:
`src/Sharp.Mediator/Sharp.Mediator.targets`, packed into its `buildTransitive` folder. Depends on
`Microsoft.Extensions.DependencyInjection.Abstractions`.

### `Sharp.Mediator.Responses`

The `Response` and `Response<T>` envelopes with a `ResponseStatus`, the `ResponseFailure` that every failure factory
returns and that alone becomes a `Response<T>`, the `Page<T>`, `CursorPage<T>`, and `Identifier<T>` values,
`ResponseError`, and the `Responses` factory. It does not depend on the mediator,
only on `Sharp.Validator.Result` for validation errors.

### `Sharp.Mediator.Validation`

Middleware that runs `Sharp.Validator` validators: `ValidationMiddleware<TMessage>`,
`ValidationMiddleware<TMessage, TResponse>`, `ValidationStreamMiddleware<TRequest, TResponse>`,
`ResponseValidationMiddleware<TMessage, TResponse>`, and the internal `ValidatorRunner` they share.
`ValidationExtensions` registers them. They are public, so the generator of every assembly that references the package
finds them in its metadata and closes them at build time. Depends on `Sharp.Mediator`, `Sharp.Mediator.Responses`, and
`Sharp.Validator`.

### `Sharp.Mediator.HttpResults`

`HttpResultExtensions` maps a response envelope to an ASP.NET Core `IResult`, and `HttpValidationErrors` with
`HttpValidationError` is the body of a 422 response. Depends on `Sharp.Mediator.Responses` and the ASP.NET Core shared
framework, not on the mediator.

### `Sharp.Mediator.SourceGenerator`

The incremental generator and diagnostic analyzers target `netstandard2.0`, depend only on Roslyn, and ship together
inside the `Sharp.Mediator` package's analyzer slot. `Generator.cs` owns the incremental pipelines.
`Generation/Discovery.cs` inspects symbols, `Generation/Model.cs` contains value-equatable models, and the
`Generation/Emitter*.cs` files write registrations, route finders, and interceptors.

`Analysis/` holds the shared handler, message, middleware, reference, and applicability checks. The generator uses
these checks to exclude unsupported inputs independently of diagnostic suppression. `Analyzer.cs`
reports the existing `SMD` diagnostics against the current source trees, from one analysis of the compilation. Source
emission reports no user diagnostics, so diagnostic locations do not invalidate its cache. Only a build that skips
analyzers, as `RunAnalyzers=false` or `RunAnalyzersDuringBuild=false` does, gets the same diagnostics from a separate
implementation output of the generator, so a declaration left out for an error still fails the build.
`Sharp.Mediator.targets` makes the two properties visible to it, and the IDE, whose live analysis runs the analyzers,
runs no implementation output. A project that references the generator without importing those targets, as a bare
`ProjectReference` can, never sees the properties, so the generator reports nothing there; a package consumer gets them
through `buildTransitive`. The builds Visual Studio starts to debug or run tests skip analyzers by a third rule, unless
warnings are errors, and the generator does not follow it: following it would make every switch between such a build
and an ordinary one recompile the project, and those builds ship nothing while the editor shows the same diagnostics.
`DiagnosticDescriptors`, in `Analyzer.cs`, owns the descriptors; `Properties/AnalyzerReleases.*.md` tracks their IDs.

### Tests

`test/` has a `{Project}.Tests` project for each project under `src/`, and projects whose conditions differ:
`Sharp.Mediator.MultiAssemblyTests` dispatches to handlers generated into two assemblies, with
`Sharp.Mediator.LibraryFixtures` as the second, and `Sharp.Mediator.AotTests` is the Native AOT canary.
`Sharp.Mediator.Fixtures` holds the helpers several test projects share, `Sharp.Mediator.NoGeneratorFixtures` is built without the generator, and `Sharp.Mediator.ScaleFixtures` declares 1000
messages for the benchmarks. `Sharp.Mediator.Benchmarks` owns the Sharp-only performance scenarios.
`Sharp.Mediator.ComparisonBenchmarks` owns the comparisons with Mediator and MediatR, in a compilation of its own
because Mediator's generator scans referenced assemblies and fixes its lifetime at compile time. Each benchmark
executable has its own `--check` entry point and artifacts directory, and keeps its hand-written message and supporting
type declarations in a `Fixtures/` folder, alongside the `Setup.cs` that builds its providers.
[CONTRIBUTING.md › Test organization](CONTRIBUTING.md#test-organization) has the rules.

## Generated code

The generator looks at one compilation at a time. It finds:

- every non-generic class that implements a handler interface, with its message and response types;
- open-generic middleware declared in the compilation;
- in the metadata of every referenced assembly that references Sharp.Mediator, built with the generator or not: the
  open-generic middleware generated code can name, which is the public middleware and the internal middleware of an
  assembly that grants this one `InternalsVisibleTo`, and the handlers whose messages it can name. This scan reads
  types, not their members, and reruns only when the references change;
- calls to the `IRequester` and `IPublisher` methods, which `IMediator` inherits, when interceptors are on.

It emits two files into the compilation:

- `SharpMediator.Registrations.g.cs`, in the namespace `Sharp.Mediator.Generated.<assembly>`.
  `GeneratedHandlerRegistrar` registers each handler under its closed handler interfaces through
  `HandlerRegistry.AddRegistrar`. `GeneratedMediatorRegistrar` passes the contract it was generated for, route finders,
  the closed handler services, and a middleware closer to `MediatorRuntime.AddAssembly`. The closer closes every
  open-generic middleware the compilation sees over its own messages and over the referenced messages whose handler's
  assembly does not record closing it. The file records, as assembly metadata, the contract it was generated for under
  the key `Sharp.Mediator.Contract` and the middleware the closer closes, in one value under
  `Sharp.Mediator.ClosedMiddleware`, which the generator of an assembly that references this one reads, so a middleware
  added to its package after this assembly was built is closed over this assembly's messages there. Each key appears
  once, since code that reads an assembly's metadata into a dictionary by key throws on a key that repeats. Metadata
  rather than an attribute of its own, so the records add no public type. They are strings, so they keep no type alive,
  though a trimmed build carries them. The referenced messages get no routes or handler services here, and an assembly
  without handlers emits the registrar only to close them. The JIT compiles the closer at startup, where it can cover
  thousands of referenced messages, so it only picks the arm of the middleware it closes, and the arm calls methods of
  at most 128 pairs, one call per pair: `TryAddClosedMiddleware`, or `MarkMiddlewareConsidered` for a pair the
  middleware's constraints exclude. Both run from a `[ModuleInitializer]`. The route finders create routes through
  `MediatorFactory`, whose generic call sites root the dispatch code of value-type messages under Native AOT. The first
  dispatch that misses compiles a finder, which loads every type it names, so a finder of more than 16 routes sorts them
  into methods by a hash of the message type's metadata name, which `Type.Name` returns at run time, and calls only the
  method of the type it is asked for. Where dynamic code is not supported, as under Native AOT, nothing compiles at run
  time, so the finder calls the methods in turn and reads no names.
- `SharpMediator.Interceptors.g.cs`, in `Sharp.Mediator.Generated.Interceptors`. Each interceptor makes one call into
  `MediatorCalls`, the one of its kind, which invokes the chain the mediator cached for the message type, or takes the
  uncached route, or publishes; what it does is the library's, so it can change without consumers being compiled again.
  The interceptor passes a struct of its message's own as a type argument, so that the call is an instance of that
  message's own, which the JIT profiles apart from every other message's, and whether the message is exactly its static
  type, which spares the runtime type check for a type nothing can derive from: a value type or a sealed class. For a
  class from another assembly, the interceptor reads whether it is sealed when the program runs, so a later version of
  that assembly that unseals it is still checked; Native AOT reads no flag, because its compiler sees every type that
  derives from the class and drops the check when none does. A call whose types the generated code cannot write, such as
  one from generic code, gets a generic interceptor of the intercepted method's type parameters, which passes a key that
  all such calls share; an inferred one, which names no message type, takes the uncached route. The compiler only
  accepts interceptors in the namespaces listed in `InterceptorsNamespaces`, which
  `src/Sharp.Mediator/Sharp.Mediator.targets` sets unless the project sets `SharpMediatorInterceptors` to `false`.

At startup `AddMediator` turns these contributions into service registrations. `AddHandlers(assembly)` runs the
registrar generated for that assembly instead of scanning it, and every registered open-generic middleware is closed by
each assembly's middleware closer. Under the JIT, a module initializer only runs once something touches its module, so
the builder runs the handler assembly's initializer itself; Native AOT runs them all at startup.

When generated code is missing, nothing falls back to reflection: `AddMediator` throws when no assembly contributed a
registrar, `AddHandlers` throws for an assembly built without the generator, and a handler registered by type throws
when no generated code declares its message. So does an event handler registered in the collection directly from an
assembly built without the generator, when no assembly declares a handler for its event, since an event without a
declared handler publishes to none. The one runtime fallback is for middleware. When a registered
open-generic middleware meets a message that no compilation could close it over, which is a message only its own
assembly can name, met by middleware that assembly cannot see, the JIT closes it with `Type.MakeGenericType` at startup.
Under Native AOT, where that is not available, startup fails with guidance instead. `AddMediator` does this only for the
handlers the service collection registers when it runs, and remembers the rest, so that creating the first mediator
fails if one of their handlers was registered after it.

## Invariants

- Dispatch follows the message's runtime type. A request or stream request goes to the handler of the nearest type in
  its class chain that declares one, and an event reaches the handlers of every type it is that declares any. The type
  arguments of a call only type the call, and a cached chain is used directly only for a message that is exactly its
  static type.
- Generated code reaches the library only through public members: the `Sharp.Mediator.Bindings` types and the
  public message, handler, and middleware interfaces. It never needs `InternalsVisibleTo`.
- Once a public member of `Sharp.Mediator.Bindings` ships, it is never removed or changed; new members are added beside
  it. Every library built against Sharp.Mediator calls these members from its generated code, so a library compiled
  against an earlier version would otherwise fail at run time with `MissingMethodException`, and its users cannot
  rebuild it. Generated code passes `MediatorRuntime.AddAssembly` the number of the contract it was generated for, so a
  later version that changes what generated code contributes reads the contracts it supersedes by that number, and
  rejects one it does not know. An assembly that only calls the mediator, whose generated code has interceptors and no
  mediator registrar, passes no number: its interceptors call `MediatorCalls`, which this rule alone keeps working,
  since its members are only ever added.
- Nothing scans an assembly. Handler registrations and routes come only from generated code, so handlers must be
  compiled by C# with the generator.
- No dispatch path calls `Type.MakeGenericType` or generates code at run time. The only runtime closing, of
  open-generic middleware under the JIT, happens at startup behind `RuntimeFeature.IsDynamicCodeSupported`.
- A request or stream request has exactly one handler. The generator reports a second one in the same compilation as
  `SMD005`. `AddMediator` rejects one registered from another assembly or by hand, and the first mediator of a
  provider rejects one registered after `AddMediator`.
- Routes, registrars, and slot indices live for the process, and nothing removes them, so assemblies loaded into a
  collectible `AssemblyLoadContext` are not supported.
- No message takes slot 0: element 0 of a mediator's chain array holds its `SparseChains`, which dispatch never reads
  as a chain.
- An assembly that initializes later can declare a type nearer to a runtime type, or another level of an event. When
  an assembly adds its route finders, `MessageRoutes.AddResolvers` replaces the routing as a whole, with one of every
  assembly's resolvers that has learned nothing yet, and forgets every event found to have a single level. It keeps
  only what no assembly can change: the record that a request or stream request type declares a handler, since
  nothing is nearer to an instance of exactly that type.
- A dispatch that resolves or walks takes one routing and answers from it alone. One that overlaps an assembly's
  initialization reaches the handlers that the routing before the assembly chooses or those that the routing after it
  chooses, never some of each, and what it learns stays in the routing it took. A dispatch that starts after the
  initializer has returned sees the assembly. Routing is process-wide, not fixed per container, so a later assembly
  changes where every container's messages go: a message whose nearer type it declares goes to that type's handler,
  which the container has to register.
- A mediator keeps a chain, or its publisher, only when every service in it is registered with at least the mediator's
  lifetime, so caching never changes what a registration's lifetime means. `ServiceRetention` reads the registrations
  as they stand when the first mediator of a provider is created, and rejects a scoped handler, middleware, or
  publisher under a Singleton mediator. Under Microsoft's container, which resolves only what the service collection
  registers, a mediator looks up a kind of middleware only when the collection registers some of that kind; under
  another container, which may resolve middleware registered through its own API, it looks up every kind.
- A warm typed call does not allocate, box, or take a lock when its handler completes synchronously, and its cost does
  not depend on how many messages the application declares. The exceptions are a publish of a value-type event with
  more than one level, which boxes the event, a stream request with a token that can be canceled, which wraps the
  handler's stream in one object so that its enumerator receives the token, and the one call in a mediator's life that
  moves the chains it keeps beside its array into an array of every slot, which allocates that array and takes the
  lock of the one it replaces.

## Boundaries

- The public API is the `Sharp.Mediator` and `Sharp.Mediator.Publishers` namespaces and the namespaces of the
  companion packages. `DefaultMediator`, the routes, the slots, `EventEntry`, and `MessageHandleBuilder` are internal,
  so dispatch can change without breaking users.
- `Sharp.Mediator.Bindings` is public only because generated code in other assemblies calls it. Its types are
  hidden from IntelliSense with `[EditorBrowsable(EditorBrowsableState.Never)]` and are not for direct use.
  The name refers to the calls generated code binds to at compile time, not to model or configuration binding.
- Generated code lives in the consumer's assembly, in `Sharp.Mediator.Generated.*` namespaces, and ends where it calls
  into `Sharp.Mediator.Bindings`.
- The generator is a build-time dependency only. `Sharp.Mediator` references it with `ReferenceOutputAssembly="false"`
  and packs it as an analyzer; no runtime assembly references it.
- `Sharp.Mediator.Responses` and `Sharp.Mediator.HttpResults` do not reference the mediator.
  `Sharp.Mediator.Validation` is the one package that joins the mediator, the envelopes, and Sharp.Validator.
- `Sharp.Mediator.HttpResults` is outside the Native AOT guarantee: it serializes with reflection-based
  `System.Text.Json` and does not set `IsAotCompatible`.

## Cross-cutting concerns

- **Native AOT and trimming**: `Sharp.Mediator`, `Sharp.Mediator.Responses`, and `Sharp.Mediator.Validation` set
  `IsAotCompatible`, so the trim and AOT analyzers run in every build, and CI builds with warnings as errors.
  Reflection is limited to the handler, middleware, and publisher types passed to the builder, whose parameters carry
  `DynamicallyAccessedMembers` annotations, and to the JIT-only middleware closing above. CI publishes
  `test/Sharp.Mediator.AotTests` with Native AOT and runs it.
- **Concurrency**: `MediatorRuntime`, `HandlerRegistry`, and the `MessageRoutes` routing are process-wide. They change
  under a lock, and dispatch reads the routing and its tables without one. Slot indices, and what dispatch has learned
  about each type, are static fields of the closed slot types, shared by every mediator in the process. Each
  `DefaultMediator` owns its chain cache, so the cache lives as long as the mediator's DI lifetime: Singleton caches
  from its first dispatch, Scoped once its scope has dispatched several times, and Transient never. Dispatch reads the
  cache without a lock. A miss writes a chain past the array's end under the lock of the array, which growing that
  array and changing the `SparseChains` it holds take too, and a chain inside the array without a lock, as a full
  fence before it checks that the array is still the mediator's. A growth checks the array again after it publishes
  its copy and carries over what was written into the array meanwhile, so no chain is lost to another dispatch growing
  the cache.
- **Errors**: The analyzers report what they can see at build time as `SMD` diagnostics: errors for shapes that cannot
  be dispatched, and warnings for code left out of the generated registrations. Configuration mistakes throw
  `InvalidOperationException` from the builder or `AddMediator`, at startup rather than at the first dispatch. A request
  whose handler is not declared or not registered throws `InvalidOperationException` at the call, whichever method sends
  it, while an event without a handler publishes to none. An object of another kind passed to a method that takes an
  `object` throws `ArgumentException`. Handler exceptions propagate as they are, except that `ParallelPublisher` throws
  several failures together in an `AggregateException`. With `Sharp.Mediator.Responses`, a handler returns a failure as
  a value instead of throwing it.
- **Performance**: The hot paths are the typed `Request`, `Publish`, and `RequestStream` calls and the generated
  interceptors. A warm call reads the chain from the mediator's slot array and invokes it, with no DI lookup. Slots go
  to messages in the order the process first dispatches them, so a busy request scope's message can hold a high slot:
  a mediator that waits for misses before it caches, as a Scoped one does, keeps an array of the slots below 32, and
  the chains of higher slots in `SparseChains`, a small table. A warm call reaches the table on the out-of-line route
  of a miss, still without a lock or an allocation, though `ScopeCacheBenchmarks` measures such a call at about ten
  times an array hit. Reading the table inline would cost every hit instead: the call it adds to each call site keeps
  the caller's arguments in callee-saved registers, which the hit then saves and restores. Once the mediator caches so
  many that an array of their slots takes no more memory than the table, it moves them into its array, and so it does
  once its dispatches have found chains in the table as often as a quarter of the highest slot there: a long-lived scope
  then hits its array as a Singleton mediator does, while a request scope rarely dispatches that often and keeps its
  small cache. A Singleton mediator lives for the process, and its array holds every slot it caches.
  `test/Sharp.Mediator.Benchmarks` measures these paths; run the matching scenario when you change one.
