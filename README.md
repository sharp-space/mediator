# Sharp.Mediator

A mediator for .NET built for speed and Native AOT, with first-class request/response, events, streaming, and
middleware.

```csharp
public sealed record GetGreeting(string Name) : IRequest<string>;

public sealed class GetGreetingHandler : IRequestHandler<GetGreeting, string>
{
    public ValueTask<string> Handle(GetGreeting message, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"Hello, {message.Name}!");
}

public sealed record UserRegistered(Guid Id) : IEvent;

public sealed class SendWelcomeEmail : IEventHandler<UserRegistered>
{
    public ValueTask Handle(UserRegistered message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

// Registers every handler the source generator found in this assembly.
services.AddMediator(x => x.AddHandlers(typeof(Program).Assembly));

var greeting = await mediator.Request(new GetGreeting("Ada"));
await mediator.Publish(new UserRegistered(Guid.NewGuid()));
```

## Features

- **Fast, allocation-free hot path.** A warm typed request or publish through `IMediator` takes a few nanoseconds
  (see Performance). Handlers return `ValueTask` and `ValueTask<T>`, so a synchronously completed request or publish
  allocates nothing, apart from boxing a value-type event that has more than one level, and the one call that moves a
  long-lived scope's cache into an array.
- **Compile-time dispatch.** A bundled source generator emits closed-generic registrations into your assembly at build
  time, so dispatch needs no reflection and no `Type.MakeGenericType`. Each message's resolved handler chain is cached
  in a slot of its own, so a warm call skips the DI lookup, and its cost does not grow with the number of messages.
- **Requests, events, and streams.** A request goes to one handler, an event to the handlers of every type it is, and
  an `IStreamRequest<TResponse>` returns `IAsyncEnumerable<TResponse>` end to end, with cancellation and a middleware
  chain of its own.
- **Middleware in registration order.** Open-generic middleware is closed over each message at build time, including
  middleware that ships in a package.
- **Explicit publisher choice.** Pick `SequentialPublisher`, the default, or `ParallelPublisher`, plug in your own, or
  override the publisher for one call without changing shared state.
- **Handlers in any number of assemblies.** Every assembly's messages join one set of dispatch tables, with no
  runtime scanning.
- **Native AOT- and trim-safe.** The bundled source generator does at build time what would otherwise need runtime
  reflection. The packages target .NET 10 and are annotated for nullable reference types.
- **Companion packages.** Response envelopes, Sharp.Validator integration, and Minimal API results.

## Packages

| Package | Description |
| --- | --- |
| [`Sharp.Mediator`](https://www.nuget.org/packages/Sharp.Mediator) | The dispatcher, middleware, and publishers, with the source generator bundled |
| [`Sharp.Mediator.Responses`](https://www.nuget.org/packages/Sharp.Mediator.Responses) | `Response` and `Response<T>` envelopes with success, error, validation, not-found, and auth status |
| [`Sharp.Mediator.Validation`](https://www.nuget.org/packages/Sharp.Mediator.Validation) | Middleware that runs Sharp.Validator and throws `ValidationException` or returns an `Invalid` response |
| [`Sharp.Mediator.HttpResults`](https://www.nuget.org/packages/Sharp.Mediator.HttpResults) | Maps `Sharp.Mediator.Responses` envelopes to ASP.NET Core `IResult` for Minimal APIs |

`Sharp.Mediator` is all most applications need. `Sharp.Mediator.Validation` and `Sharp.Mediator.HttpResults` each
bring in `Sharp.Mediator.Responses`.

## Install

```bash
dotnet add package Sharp.Mediator
dotnet add package Sharp.Mediator.Responses     # optional
dotnet add package Sharp.Mediator.Validation    # optional
dotnet add package Sharp.Mediator.HttpResults   # optional, for ASP.NET Core
```

Outside ASP.NET Core, the Quick start also needs the DI container:

```bash
dotnet add package Microsoft.Extensions.DependencyInjection
```

Requires .NET 10 or later. Handlers must be written in C#, because the bundled source generator registers them.
Package versions follow Semantic Versioning independently of .NET releases, starting at 1.0.0. Patch releases fix
bugs; minor releases add backward-compatible features; major releases introduce breaking changes or raise minimum
SDK, compiler, or runtime requirements. All packages in this repository share a version; Validator, Mediator, and
Messenger release independently. The minimum supported runtime remains .NET 10. Changes are listed in the
[release notes](https://github.com/sharp-space/mediator/releases).

## Quick start

```csharp
// Register the mediator and the handler.
var services = new ServiceCollection();
services.AddMediator(x => x.AddHandler<PingHandler>());

// Dispatch a request in a scope.
using var serviceProvider = services.BuildServiceProvider();
using var scope = serviceProvider.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

var reply = await mediator.Request(new Ping("world"));
Console.WriteLine(reply); // hello, world

// A request and its handler.
public sealed record Ping(string Name) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping message, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"hello, {message.Name}");
}
```

> **Note:** Handlers return `ValueTask` and `ValueTask<T>`, not `Task`, so a synchronously completed request or publish
> allocates nothing. Await the returned value exactly once: don't store it and await it twice, and call `.AsTask()`
> when you need to combine it with `Task.WhenAll`.

## Requests

A request goes to exactly one handler. Use `IRequest` when nothing comes back and `IRequest<TResponse>` when the
handler returns a value.

### Send a request

```csharp
public sealed record Save(string Path) : IRequest;

public sealed class SaveHandler : IRequestHandler<Save>
{
    public ValueTask Handle(Save message, CancellationToken cancellationToken)
    {
        // ...
        return ValueTask.CompletedTask;
    }
}

await mediator.Request(new Save("/tmp/a"));          // typed, no response
await mediator.Request<Ping, string>(new Ping("x")); // typed, with a response
await mediator.Request(new Ping("x"));               // by interface: the response type is inferred, nothing is boxed
await mediator.Request((object) new Ping("x"));      // by runtime type
```

The mediator picks the right path for each call. A request goes to the handler of the nearest type in its class chain
that declares one, whichever method sends it.

> **Note:** A request type has exactly one response type. Implementing both `IRequest<A>` and `IRequest<B>` (`SMD001`),
> or `IRequest` together with `IRequest<T>` (`SMD002`), on the same type is a build error.

## Events

An event goes to every handler registered for it, and to the handlers of its base classes and interfaces. Publishing
returns nothing.

### Publish an event

```csharp
public sealed record UserRegistered(Guid Id) : IEvent;

public sealed class SendWelcomeEmail : IEventHandler<UserRegistered>
{
    public ValueTask Handle(UserRegistered message, CancellationToken cancellationToken)
    {
        // ...
        return ValueTask.CompletedTask;
    }
}

await mediator.Publish(new UserRegistered(Guid.NewGuid()));
```

One class can handle several event types: implement `IEventHandler<TEvent>` once for each type.

### Handle an event by its base class or interface

```csharp
public interface IAuditable : IEvent;

public abstract record OrderEvent(Guid OrderId) : IEvent;

public sealed record OrderPlaced(Guid OrderId) : OrderEvent(OrderId), IAuditable;

// Runs the handlers of OrderPlaced, then OrderEvent, then IAuditable, once each.
await mediator.Publish(new OrderPlaced(orderId));
```

An event reaches the handlers of every type it is that declares any: its own type, its base classes, and its
interfaces. Each of those types is a level, with its own handlers and middleware. Levels run from the event's own type
outward, then the interfaces by full name. `Publish(placed)`, `Publish<OrderEvent>(placed)`, and
`Publish((object) placed)` all reach the same handlers, so publish an event once rather than once for each type. A
handler for `IEvent` itself receives every event.

> **Note:** An event without a handler publishes to none, whether no assembly declares a handler for any type it is or
> none of the declared ones is registered: the default publisher does nothing, and any other publisher receives the
> publish with no handlers. `Publish(object)` throws `ArgumentException` for an object that is not an event.

## Stream requests

A stream request goes to one handler, which returns `IAsyncEnumerable<TResponse>`. It runs through a middleware chain
of its own and honors cancellation.

### Stream a response

```csharp
public sealed record TailLog(string Path) : IStreamRequest<string>;

public sealed class TailLogHandler : IStreamRequestHandler<TailLog, string>
{
    public async IAsyncEnumerable<string> Handle(
        TailLog message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in File.ReadLinesAsync(message.Path, cancellationToken))
            yield return line;
    }
}

await foreach (var line in mediator.RequestStream(new TailLog("/var/log/app.log"), cancellationToken))
    Console.WriteLine(line);
```

`RequestStream` returns the handler's stream without buffering it. When the call's token can be canceled, the
handler's enumerator receives it too, as with `WithCancellation`, so a handler may return a stream that observes only
the token of its enumerator, such as EF Core's `AsAsyncEnumerable()`. A caller that enumerates with a different token
cancels the stream through either. Passing the token on takes one small object per call; with no token, `RequestStream`
returns the handler's stream itself.

## `IRequester` and `IPublisher`

`IMediator` is `IRequester`, for requests and stream requests, plus `IPublisher`, for events. A class that only does
one of those can depend on just that half.

### Depend on one half of the mediator

```csharp
public sealed class CheckoutEndpoint(IRequester requester) { /* ... */ }
public sealed class OrderProjection(IPublisher publisher) { /* ... */ }
```

In one scope, `IMediator`, `IRequester`, and `IPublisher` resolve to the same instance. `IPublisher` is what you call
to publish. `IEventPublisher`, described under Publishers, is the strategy that decides how the handlers then run.

## Middleware

Middleware wraps a handler: it runs code before and after the handler, and continues by calling
`next(message, cancellationToken)`. Each kind of message has one middleware shape.

### Pick the middleware shape

| Message | Middleware |
| --- | --- |
| `IRequest` | `IMiddleware<TMessage>` |
| `IEvent` | `IMiddleware<TMessage>`, once for each handler |
| `IRequest<TResponse>` | `IMiddleware<TMessage, TResponse>` |
| `IStreamRequest<TResponse>` | `IStreamMiddleware<TRequest, TResponse>` |

`AddMiddleware` registers every shape, and a class that implements more than one is registered under each.

`IMiddleware<TMessage>` does not run for requests with a response. To cover every request, register both shapes as two
classes, the way `ValidationMiddleware<>` and `ValidationMiddleware<,>` do. A closed middleware that targets the wrong
shape, such as `IMiddleware<Ping>` for a `Ping : IRequest<int>`, is reported as `SMD012`.

### Write middleware

```csharp
// For void requests and events
public sealed class LoggingMiddleware<TMessage>(ILogger<LoggingMiddleware<TMessage>> log)
    : IMiddleware<TMessage> where TMessage : IMessage
{
    public async ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken)
    {
        log.LogInformation("dispatching {Message}", typeof(TMessage).Name);
        try { await next(message, cancellationToken); }
        catch (Exception exception) { log.LogError(exception, "dispatch failed"); throw; }
    }
}

// For requests that return a response
public sealed class CacheMiddleware<TMessage, TResponse>(IMemoryCache cache)
    : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
{
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        var key = ComputeKey(message);
        if (cache.TryGetValue(key, out TResponse? hit)) return hit!;

        var result = await next(message, cancellationToken);
        cache.Set(key, result, TimeSpan.FromMinutes(5));
        return result;
    }
}
```

### Register middleware

```csharp
services.AddMediator(builder => builder
    .AddMiddleware(typeof(LoggingMiddleware<>))         // open generic
    .AddMiddleware<CacheMiddleware<GetUser, User>>());  // closed
```

Middleware runs in registration order: the first registered wraps the second, which wraps the third, which wraps the
handler.

- A closed middleware registers every closed `IMiddleware<>` and `IMiddleware<,>` interface it implements, at run
  time, from any assembly, and safely under trimming and Native AOT.
- An open-generic middleware is closed over each message it applies to by the source generator, with full constraint
  checking: `class`, `struct`, `unmanaged`, `new()`, and interface and base-type constraints, including
  self-referential ones like `where TMessage : IMessage<TResponse>` and ones a message meets through variance, such as
  a `List<string>` response for `where TResponse : IEnumerable<object>`. Messages that don't satisfy the constraints are
  skipped. The definition's generic parameters must mirror the middleware interface's one for one, in the same order.
  Fixed, reordered, or unused parameters, and types nested in a generic type, are reported as `SMD011`; register the
  closed middleware types instead. A private, protected, or file-local definition, which generated code cannot name, is
  reported as `SMD010`: the JIT closes it by reflection at startup, and Native AOT fails at startup with guidance.
- Registering the same middleware twice, or calling `AddMediator` twice, never makes a middleware run twice.

### Ship middleware in a package

A package that ships open-generic middleware only has to make it public. The generator in every assembly that references
the package finds the package's public open-generic middleware in its metadata, whether or not the package was built
with the generator, and closes it at build time over the messages that assembly can name. This keeps the middleware
Native AOT-safe across assemblies:

```csharp
public sealed class AuditMiddleware<TMessage, TResponse>(IAuditLog log)
    : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
{
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        await log.Write(message, cancellationToken);
        return await next(message, cancellationToken);
    }
}

public static class AuditRegistration
{
    public static MediatorBuilder AddAudit(this MediatorBuilder builder) =>
        builder.AddMiddleware(typeof(AuditMiddleware<,>));
}
```

An internal middleware is closed by its own assembly's generator, and by assemblies it grants `InternalsVisibleTo`.

The generator closes every middleware it finds, whether or not the application registers it, and a closure takes effect
only for a registered middleware. One the application never registers costs only its closed types in the binary.

### Wrap a stream

Stream requests use `IStreamMiddleware<TRequest, TResponse>`, registered with `AddMiddleware` like the others:

```csharp
public sealed class StreamLoggingMiddleware<TRequest, TResponse>
    : IStreamMiddleware<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest message, StreamMessageHandle<TRequest, TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var item in next(message, cancellationToken).WithCancellation(cancellationToken))
        {
            count++;
            yield return item;
        }
        // log count
    }
}
```

The stream a middleware returns must honor the token it is called with, as this one does through
`[EnumeratorCancellation]`: the mediator passes the call's token to the enumerator of the handler's stream at the end
of the chain, not to the streams of middleware.

## Publishers

A publisher decides how the handlers of an event run. The default, `SequentialPublisher`, runs them one after another.
`SequentialPublisher` and `ParallelPublisher` are in the `Sharp.Mediator.Publishers` namespace.

### Run event handlers in parallel

```csharp
services.AddMediator(x => x.WithEventPublisher<ParallelPublisher>());
```

| Publisher | Runs handlers | When a handler fails |
| --- | --- | --- |
| `SequentialPublisher` (default) | One after another | The rest don't run |
| `ParallelPublisher` | Genuinely asynchronous handlers together | Every handler still runs |

Handlers of one event share a DI scope, so `SequentialPublisher` keeps scoped dependencies such as an EF Core
`DbContext` safe. Choose `ParallelPublisher` only when handlers do not share scoped state. With `ParallelPublisher`,
awaiting `Publish` rethrows a single failure as it is, and throws several together in an `AggregateException`. Both
allocate nothing when every handler completes synchronously, except that a value-type event with more than one level
is boxed once for each publish.

### Choose a publisher for one call

```csharp
await mediator.Publish(new UserRegistered(id), new MyCustomPublisher());
```

The override applies to that call only: it does not construct the configured publisher, and does not change later
`Publish` calls on the same mediator.

### Write a publisher

Implement `IEventPublisher`. It is called once for each publish, with an executor for every handler the event reaches,
across all the types it is, in level order. Each executor already wraps its handler in that type's middleware, so a
publisher only decides when each one runs:

```csharp
public sealed class FaultTolerantPublisher(ILogger<FaultTolerantPublisher> log) : IEventPublisher
{
    public async ValueTask Publish(
        IReadOnlyList<EventHandlerExecutor> executors, IEvent message, CancellationToken cancellationToken)
    {
        foreach (var executor in executors)
        {
            try { await executor.Invoke(message, cancellationToken); }
            catch (Exception exception)
            {
                log.LogError(exception, "handler {Handler} failed", executor.Handler.GetType());
            }
        }
    }
}
```

## Registration

`AddMediator` registers the mediator, the handlers, and the middleware in an `IServiceCollection`, and checks the
configuration when it runs, so wiring mistakes surface at startup rather than at the first dispatch.

### Configure the mediator

```csharp
services.AddMediator(builder => builder
    // The lifetime of every registration this builder produces, wherever the call appears
    .WithLifetime(ServiceLifetime.Transient)

    // Register handlers one at a time, whatever kind of message they handle...
    .AddHandler<PingHandler>()
    .AddHandler<SendWelcomeEmail>()
    .AddHandler<TailLogHandler>()

    // ...or every handler the source generator found in an assembly
    .AddHandlers(typeof(Program).Assembly)

    // Replace the default publisher
    .WithEventPublisher<ParallelPublisher>());
```

| Option | Default | Description |
| --- | --- | --- |
| `WithLifetime` | `ServiceLifetime.Scoped` | The lifetime of the mediator, the handlers, the middleware, and the publisher |
| `WithEventPublisher<TPublisher>` | `SequentialPublisher` | The publisher that runs event handlers |

A handler, middleware, or publisher you register yourself keeps the lifetime you give it. One that lives shorter than
the mediator, such as a Transient handler under the default Scoped mediator, is resolved for each dispatch instead of
cached, wherever it is registered. A Scoped one under a Singleton mediator is rejected, since a Singleton mediator
resolves from the root provider, where a scoped service would live as long as the process. So is a Transient one whose
implementation is disposable, since the root provider keeps every disposable instance it creates until it is disposed.
That check reads the implementation type a registration names:
`AddTransient<IRequestHandler<Ping, string>, PingHandler>(_ => new PingHandler())` names `PingHandler`, but
`AddTransient<IRequestHandler<Ping, string>>(_ => new PingHandler())` names only the service, so a disposable handler
registered that way is not caught. A Scoped or Transient mediator resolves from the provider it was resolved from, so
resolve it from a scope, as ASP.NET Core does for each request: resolved from the root provider, every disposable
Transient handler, middleware or publisher it resolves stays with that provider until the provider is disposed, and
nothing rejects that.

`AddMediator` throws at startup when no generator-compiled assembly declares a handler, rather than registering a
mediator that cannot dispatch anything. A handler registered explicitly that the generator could not see fails the
same way, and so does an event handler registered in the collection directly from an assembly built without the
generator when no assembly declares a handler for its event: publishing that event would never reach it.

`AddMediator` returns the `IServiceCollection`, so it composes with the rest of your service registration:

```csharp
services
    .AddMediator(x => x.AddHandlers(typeof(Program).Assembly))
    .AddSingleton<IClock, SystemClock>();
```

### Call `AddMediator` more than once

Repeated calls on the same `IServiceCollection` can add handlers and middleware. The first call fixes the lifetime and
the publisher set with `WithEventPublisher`; later calls inherit what they omit, accept the same explicit values, and
reject different ones. An `IEventPublisher` registered directly before `AddMediator` keeps precedence. Middleware from
any call also applies to messages from assemblies that initialize during a later call, so the order of module
registrations does not change which middleware runs.

Handler registration adds to what is there, with guardrails: the same handler implementation is registered once,
distinct event handlers all run, and a second request or stream handler for the same message is rejected. Two handlers
in the same compilation are reported as `SMD005` at build time; handlers that conflict across assemblies or through
manual registration are rejected when `AddMediator` runs, or, for one registered after it, when the first mediator is
created.

## Handlers in multiple assemblies

Handlers can live in any number of assemblies, each compiled with the bundled generator, which is automatic for any
project that references the `Sharp.Mediator` package. Every assembly's messages join one set of dispatch tables, and
runtime-typed dispatch walks the message's type and base types across all of them:

- `Request(object)`, `Publish(object)`, and the interface-typed calls work for every assembly's messages.
- When message types inherit from each other, a request goes to the handler of the nearest type in its class chain
  that declares one, whichever assembly declared it, so a message with no handler of its own reaches its nearest
  ancestor's. An event reaches every type it is that declares handlers, in any assembly.
- An assembly can initialize while the application is dispatching, as one loaded on demand does when it is first
  used. Its messages join the tables of every container at once. A dispatch that overlaps its initialization reaches
  the handlers chosen without the assembly or those chosen with it, never some of each, and every dispatch after it
  sees the assembly. A message whose nearer type the assembly declares goes to that type's handler from then on, which
  the container has to register.
- Open-generic middleware applies to every assembly's messages, and is closed at build time in each assembly that can
  name both the middleware and the message. The assembly that declares a handler closes its own middleware and the
  public middleware of the assemblies it references over its messages. An assembly that references it, such as the
  application, closes the middleware it sees over its public messages, so middleware declared in the application
  reaches a handler library's messages, and so does a package's middleware that only the application references, or
  that was added to the package after the library was built: each assembly records the middleware it closed, and the
  application closes the rest. This makes the application's generated code grow with the messages of the assemblies it
  references, as it does with its own.
- What no assembly can close is a message only its own assembly can name, such as an internal one, met by middleware
  that assembly cannot see. `AddMediator` closes it only when the container registers the message's handler, so a
  referenced library's internal messages that the application never registers need nothing. Under the JIT, startup
  closes it by reflection. Native AOT has no runtime closing, so startup fails with guidance, even when generic
  constraints would have excluded the message anyway. Make the message public, declare the middleware public in an
  assembly the handler's assembly references, or register the closed middleware types explicitly.
- Such a handler has to be registered before `AddMediator` runs, because a container cannot take the closed middleware
  once it is built. When one is registered later, creating the first mediator fails with guidance instead of running
  the handler without its middleware. Calling `AddMediator` again after registering it closes the middleware.

## Companion packages

Three optional packages work with the mediator: response envelopes, validation, and HTTP results for Minimal APIs.

### `Sharp.Mediator.Responses`

A `Response` envelope lets a handler return success, a domain error, validation errors, not found, or an auth failure
without throwing.

```csharp
public sealed record CreateUser(string Email) : IRequest<Response<UserId>>;

public sealed class CreateUserHandler : IRequestHandler<CreateUser, Response<UserId>>
{
    public ValueTask<Response<UserId>> Handle(CreateUser command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.Email))
            return new(Responses.Invalid("Email", "REQUIRED", "Email is required"));

        return new(new UserId(Guid.NewGuid()));
    }
}
```

`ResponseStatus` is `Unspecified`, `Success`, `Error`, `Invalid`, `NotFound`, `Unauthenticated`, or `Forbidden`.
`Unspecified` is zero, so a response constructed directly is not implicitly successful: use a `Responses` factory
method or set `Status` explicitly. For the common shapes of a value, `Page<T>` holds a page of items and the count
across every page, `CursorPage<T>` a page of items and the cursor of the next one, and `Identifier<T>` the id of what
an operation created or found. A handler returns them as `Response<Page<T>>` and so on, built by
`Responses.Page`, `Responses.Cursor`, and `Responses.Id`, or converted from the value itself.

The factories for an error, invalid input, a missing resource, and an auth failure return a `ResponseFailure`, which
becomes a `Response`, or a `Response<T>` of any value, where it is returned. `Responses.Success()` returns a valueless
`Response`, which has no value to carry and so does not become a `Response<T>`: return the value instead. To pass on
the failure of another response, return `ResponseFailure.From(response)`, which rejects one that succeeded.

Only a successful `Response<T>` has a `Value`. Reading it from any other, including one whose status was never set,
throws `InvalidOperationException`, as the explicit `(T)` conversion does, so a failure is never read as a null or zero
value. Where a response can fail, check `IsSuccess` or call `TryGetValue`.

### `Sharp.Mediator.Validation`

Runs a message's [Sharp.Validator](https://github.com/sharp-space/validator) validators before its handler: every
`IValidator<T>` registered for it, such as those Sharp.Validator's `AddValidators` registers from an assembly.
Registration is split by request shape:

```csharp
services.AddMediator(builder => builder
    .AddRequestValidationMiddleware()   // throws for invalid void requests
    .AddResponseValidationMiddleware()  // returns an Invalid response on failure
    .AddHandler<CreateUserHandler>());

services.AddValidators(typeof(Program).Assembly);
```

| Method | Validates | On failure |
| --- | --- | --- |
| `AddRequestValidationMiddleware()` | Void `IRequest` messages | Throws `ValidationException` |
| `AddThrowingResponseValidationMiddleware()` | Every `IRequest<TResponse>` | Throws `ValidationException` |
| `AddResponseValidationMiddleware()` | `IRequest<TResponse>` whose `TResponse` implements `IResponse` and has a public parameterless constructor | Returns an `Invalid` response |
| `AddStreamValidationMiddleware()` | `IStreamRequest<TResponse>`, before the stream starts | Throws `ValidationException` on the first `MoveNextAsync` |
| `AddValidationMiddleware()` | The request-only, throwing response, and stream registrations together | As each of those |

`AddResponseValidationMiddleware()` does not validate other typed responses, and a stream has no envelope to return
failures in. The request-only and stream registrations combine with either response policy, in either order and
across repeated `AddMediator` calls, and registering one twice has no effect. The two response policies are
alternatives: combining `AddValidationMiddleware()` or `AddThrowingResponseValidationMiddleware()` with
`AddResponseValidationMiddleware()` on one service collection throws during configuration. Events are not validated.

Only `Error`-severity validation entries block dispatch; `Warning` and `Information` entries alone don't, matching
Sharp.Validator's `ValidationResult`. When an error is present, every recorded entry goes into the
`ValidationException` or the `Invalid` response.

Validators of one message run one at a time, in registration order, and all of them run. They share the message's DI
scope, so scoped dependencies such as a `DbContext` are safe to use from several validators.

### `Sharp.Mediator.HttpResults`

Maps a `Response` to an `IResult` in a Minimal API:

```csharp
app.MapPost("/users", async (CreateUser command, IMediator mediator) =>
    await mediator.Request(command).ToResult());
```

| `ResponseStatus` | HTTP response |
| --- | --- |
| `Unspecified` | Throws `InvalidOperationException`: the response was not initialized |
| `Success` | 200, with the value as the body for `Response<T>` |
| `Error` | 400, with `{ "code", "message" }` when the response has an error |
| `Invalid` | 422, with `{ "errors": [...] }` |
| `NotFound` | 404, with `{ "code", "message" }` when the response has an error |
| `Unauthenticated` | 401, with `{ "code", "message" }` when the response has an error |
| `Forbidden` | 403, with `{ "code", "message" }` when the response has an error |

The body of a 422 response is an object with one entry for each validation error, in the order the response holds
them:

```json
{
  "errors": [
    { "propertyName": "Email", "message": "Email is required", "code": "REQUIRED", "severity": "Error" }
  ]
}
```

With ASP.NET Core's default JSON options, each entry has all four fields: `message` and `code` are `null` when the
validator gave none, and `severity` is the name of the entry's severity: `"Error"`, `"Warning"`, or `"Information"`.
The bodies are plain JSON, not RFC 9457 problem details, and are written with the application's JSON options like any
other Minimal API result. A naming policy or an ignore condition the application configures changes the names and the
`null` fields shown here.

A `Response<T>` writes its value on success, whatever the value is: `Response<Page<T>>` writes `{ "items", "count" }`,
`Response<CursorPage<T>>` writes `{ "items", "nextCursor" }`, and `Response<Identifier<T>>` writes `{ "id" }`, named by
the application's JSON options like the error bodies. The envelope's own properties are `[JsonIgnore]`. A response
typed as `IResponse`, which has no value, selects the status-only overload. An error, not-found, or auth response
without an error has an empty body, and a bare `Forbidden` is a plain 403 that neither requires authentication
services nor invokes a forbid scheme.

## Coming from MediatR

Called through `IMediator`, a warm request runs about 30 to 40 times faster than in MediatR 12.5.0 and allocates
nothing (see Performance).

| MediatR | Sharp.Mediator |
| --- | --- |
| `IRequest<T>` / `IRequest` | `IRequest<T>` / `IRequest` |
| `IRequestHandler<T, R>` / `IRequestHandler<T>` | `IRequestHandler<T, R>` / `IRequestHandler<T>` |
| `INotification` / `INotificationHandler<N>` | `IEvent` / `IEventHandler<E>` |
| `IStreamRequest<T>` | `IStreamRequest<T>`, with a separate middleware chain |
| `IPipelineBehavior<T, R>` | `IMiddleware<T, R>` for requests with a response, `IMiddleware<T>` for void requests and events |
| `INotificationPublisher` | `IEventPublisher`, with `SequentialPublisher` and `ParallelPublisher` included |
| `NotificationHandlerExecutor` | `EventHandlerExecutor` |
| `ISender` / `IPublisher` | `IRequester` / `IPublisher` |
| `mediator.Send(request)` | `mediator.Request(request)` |
| `mediator.Publish(message)` | `mediator.Publish(message)` |

These behaviors differ:

- Middleware runs in registration order, the first registered outermost. There is no split between
  `IPipelineBehavior` and `IRequestPreProcessor`: one middleware shape covers both.
- The publisher passed to `Publish(message, publisher)` applies to that call only. It changes no shared state and does
  not affect later calls on the same mediator.
- A request or stream request goes to the handler of the nearest type in its class chain that declares one, whichever
  method sends it. With `Square : Shape` and a handler for each, `Request<Shape, string>(square)`, `Request(square)`,
  and `Request((object) square)` all run `Square`'s handler: the type arguments only type the call. If the nearest
  handler is declared but this container does not register it, the call fails instead of falling back to a base
  class's handler. A request or stream handler declared for an interface is a build error (`SMD008`), because no
  runtime type is an interface.
- An event reaches the handlers of every type it is, as with MediatR's contravariant handlers, whichever method
  publishes it, so `foreach (IEvent e in events) await mediator.Publish(e);` works. Each type is a level with its own
  middleware. `SequentialPublisher` runs the levels in order and stops at the first failure, `ParallelPublisher` starts
  every level at once, and a custom `IEventPublisher` receives every level's handlers in one call, as MediatR's
  `INotificationPublisher` does.
- Handlers spread across several assemblies are first-class, as described under Handlers in multiple assemblies;
  MediatR relies on scanning assemblies with reflection at run time.
- Open generic handlers are not supported, because generated code needs every closed message at build time. Make the
  generic handler abstract and derive one handler for each message: `sealed class IntBoxHandler : BoxHandler<int>;`.

## Native AOT and trimming

Sharp.Mediator is Native AOT- and trim-safe. The source generator bundled in the `Sharp.Mediator` package emits
closed-generic registrations and runtime-type routes at build time, so the `object`-typed calls (`Request(object)`,
`Publish(object)`, and `RequestStream(object)`) need no `Type.MakeGenericType`. The `Sharp.Mediator`,
`Sharp.Mediator.Responses`, and `Sharp.Mediator.Validation` packages set `IsAotCompatible`, and every change is
checked by publishing a test application with Native AOT, with warnings as errors, and running it.

Both ways of registering handlers are Native AOT-safe. Explicit calls such as `AddHandler<THandler>()` are
annotated so the handler's interfaces and constructor survive trimming. `AddHandlers(Assembly)` runs the assembly's
generated, closed-generic registrations instead of scanning it.

Those registrations only come from generated code, so handlers must be compiled by C# with the bundled generator.
`AddHandlers` rejects an assembly built without it, such as an F# or Visual Basic project, under both the JIT and
Native AOT.

Open-generic middleware is closed at build time, also across assemblies, as described under Middleware and Handlers in
multiple assemblies. The one Native AOT constraint is a message only its own assembly can name, such as an internal one,
met by open-generic middleware that assembly cannot see, when the container registers its handler: register the closed
middleware types for it.

> **Note:** `Sharp.Mediator.HttpResults` is not part of the Native AOT guarantee: it maps through reflection-based
> `System.Text.Json`.

> **Note:** The generated registrations and dispatch tables live for the process, so unloading a collectible
> `AssemblyLoadContext` is not supported. Load plugins into a non-collectible load context.

## Performance

Called through each library's `IMediator`, Sharp.Mediator is 3.5 to 11 times faster than
[Mediator](https://github.com/martinothamar/Mediator) and about 17 to 57 times faster than MediatR 12.5.0, the last
MediatR release under Apache-2.0, in every scenario measured:

| Scenario | Messages | Sharp.Mediator | Mediator 3.0.2 | MediatR 12.5.0 |
| --- | ---: | ---: | ---: | ---: |
| Request with a response | 10 | 2.85 ns | 12.9 ns | 82.6 ns, 200 B |
| | 1000 | 2.22 ns | 19.2 ns | 92.1 ns, 200 B |
| Request without a response | 10 | 1.65 ns | 12.0 ns | 74.0 ns, 128 B |
| | 1000 | 1.71 ns | 18.8 ns | 84.3 ns, 128 B |
| Request passed as `object` | 10 | 5.67 ns | 21.3 ns | 95.1 ns, 272 B |
| | 1000 | 5.18 ns | 31.4 ns | 109 ns, 272 B |
| Event with two handlers | 10 | 4.58 ns | 21.1 ns | 149 ns, 440 B |
| | 1000 | 4.68 ns | 24.8 ns | 166 ns, 440 B |
| Request through one middleware | 10 | 3.35 ns | 18.2 ns | 164 ns, 384 B |
| | 1000 | 3.36 ns | 24.3 ns | 192 ns, 384 B |

Each cell is the time of one call, the median of four processes on Linux x64, and, where it is not zero, the memory the
call allocates. Each library runs a warm Singleton mediator whose handlers complete synchronously, in an application of
that many messages of each kind: requests with a response, requests without one, and events with two handlers. MediatR's
API has its handlers return a `Task`, so the memory of a MediatR request with a response includes the 72 B `Task` its
handler returns. Each call dispatches the last message of its kind. With 10, Mediator finds the handler through a switch
on the message type, in which that message is the last case: a request with a response takes 10.0 ns for the first
message, while a void request or an event takes the same for either. With 1000, Mediator looks the handler up in a
dictionary.

Typed calls on a warm mediator take the handler chain from a cache and allocate nothing when the handler completes
synchronously, except for the box of a value-type event with more than one level and the one call that moves a
scope's cache into an array, both below, and the object that passes a stream request's token on (see Stream
requests). Their cost does not depend on how many messages the application declares.
[ARCHITECTURE.md](https://github.com/sharp-space/mediator/blob/main/ARCHITECTURE.md) describes the caches behind these
numbers.

- An event with more than one level looks its levels up by its runtime type on each publish. A publisher other than
  `SequentialPublisher` receives every level's handlers in one array, which a warm mediator keeps. A value-type event
  of more than one level is boxed once for each publish.
- The mediator's lifetime decides what it caches. A Singleton mediator caches from its first dispatch. A Scoped
  mediator starts caching once its scope has dispatched several times, so a request scope that dispatches once or twice
  resolves through DI and allocates no cache, and a long-lived scope that keeps dispatching becomes as fast as a
  Singleton mediator after one call that allocates the cache's array. A Transient mediator never caches, so each call
  gets a fresh handler. A message whose handler or middleware is registered with a shorter lifetime than the
  mediator's is never cached.
- The bundled generator intercepts the generic calls on `IMediator`, so they skip generic virtual dispatch, and a call
  whose message type is known at compile time can be inlined into its caller. Interception is on in every project that
  gets the package, directly or through a project reference or another package, such as a web project over an
  application layer. A call is left as written when it passes the message of an inferred call by name or as an
  interface, an abstract class, or a struct, passes a publisher, or sits inside an expression tree, such as a Moq setup,
  which then still names the mediator's own method. Any other `IMediator` implementation, such as a test double, still
  receives the original call, and a message that is not exactly its static type takes the same route as without
  interception.
- The interface-typed `Request<TResponse>(message)`, and `Request(object)`, `Publish(object)`, and
  `RequestStream(object)`, add one lookup by runtime type. The first dispatch of a runtime type creates its route,
  walking its base types if it inherits its handler, and later dispatches find the route directly.
  `Request<TResponse>(message)` does not box the `ValueTask<T>`; the `object` return type boxes or wraps the result as
  the signature requires.

### Turn off call interception

```xml
<PropertyGroup>
  <SharpMediatorInterceptors>false</SharpMediatorInterceptors>
</PropertyGroup>
```

| Option | Default | Description |
| --- | --- | --- |
| `SharpMediatorInterceptors` | `true` | MSBuild property. `false` keeps every call on the `IMediator` interface |

To turn interception off for one build, pass `-p:SharpMediatorInterceptors=false` to `dotnet build`.

### Run the benchmarks

The repository separates Sharp-only benchmarks from comparisons with Mediator and MediatR. In a clone of the
repository:

```bash
# Sharp-only scenarios
dotnet run -c Release --project test/Sharp.Mediator.Benchmarks
# Comparisons with Mediator and MediatR
dotnet run -c Release --project test/Sharp.Mediator.ComparisonBenchmarks -- --filter '*'
# Smoke checks, without timing
dotnet run -c Release --project test/Sharp.Mediator.Benchmarks -- --check
dotnet run -c Release --project test/Sharp.Mediator.ComparisonBenchmarks -- --check
```

The comparison above comes from `DispatchBenchmarks`, `VoidRequestBenchmarks`, `PublishBenchmarks`, and
`MiddlewareBenchmarks` in `test/Sharp.Mediator.ComparisonBenchmarks`, which gives each library the same generated
messages and one lifetime and calls each through its `IMediator`. Their `Mediator_Concrete` benchmarks call Mediator
through the per-message overloads of its generated class instead, which Mediator recommends for speed; code that depends
on that class cannot mock it, so the table compares the interfaces. The `MessageCount` environment variable sets the
application size, 1000 by default and 10 for the table's smaller rows, and `MediatorLifetime=Scoped` compares on the
Scoped lifetime.

In `test/Sharp.Mediator.Benchmarks`, `RequestScopeBenchmarks` measures a new scope for each request, the shape of a web
app on the default lifetime, with the requests or events it dispatches, and `LifetimeBenchmarks` measures such a
request under each lifetime.
`ScaleBenchmarks` measures an application with 1000 messages, `ScopeCacheBenchmarks` a busy request scope in it,
`LongLivedScopeBenchmarks` a scope that keeps dispatching in it, `StartupBenchmarks` what those messages add before
`Main`, `CompositionBenchmarks` what `AddMediator` and the first mediator cost it with and without open-generic
middleware, and `FirstDispatchBenchmarks` the first dispatch after it.
The other scenarios use a small application and one warm scope, among them `EventHandlerCountBenchmarks`, which
measures a publish by how many handlers its event has, and several events published in turn.

## Diagnostics

The bundled analyzers report `SMD`-prefixed diagnostics. Each one has a page with its cause, its fix, and how
to suppress it: [diagnostics](https://github.com/sharp-space/mediator/blob/main/doc/diagnostics/README.md). A build
that skips analyzers, with `RunAnalyzers` or `RunAnalyzersDuringBuild` set to `false`, gets the same diagnostics from
the source generator, so a declaration it leaves out for an error still fails the build. The builds Visual Studio starts
to debug or run tests skip analyzers on their own unless warnings are errors, and they do not get them; the editor
still shows them.

## Contributing

Bug reports and pull requests are welcome — see
[CONTRIBUTING.md](https://github.com/sharp-space/mediator/blob/main/CONTRIBUTING.md), which also has the code of
conduct. Report security problems as described in the
[security policy](https://github.com/sharp-space/mediator/security/policy).

## License

Sharp.Mediator is licensed under the [MIT License](https://github.com/sharp-space/mediator/blob/main/LICENSE).
