using Sharp.Mediator.Fixtures;
using Sharp.Mediator.LibraryFixtures;

namespace Sharp.Mediator.MultiAssemblyTests.Fixtures;

// Application-side counterparts to the Fixtures library: the whole point of this assembly is that its generator only
// sees these types, while the library's generator only sees its types — every test below exercises the seam between
// the two compilations.

public sealed record ApplicationPing(int Value) : IRequest<int>;

public sealed class ApplicationPingHandler : IRequestHandler<ApplicationPing, int>
{
    public ValueTask<int> Handle(ApplicationPing message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(message.Value);
}

/// <summary>
/// Derives from the library's non-sealed command and has its own handler; non-sealed so a further-derived, unhandled command can exercise
/// the base-type walk.
/// </summary>
public record DerivedCommand(string Origin) : BaseCommand(Origin);

public sealed class DerivedCommandHandler : IRequestHandler<DerivedCommand, string>
{
    public ValueTask<string> Handle(DerivedCommand message, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"derived:{message.Origin}");
}

/// <summary>Derives from <see cref="DerivedCommand" /> but has no handler — dispatch must walk to the nearest ancestor's.</summary>
public sealed record UnhandledDerivedCommand(string Origin) : DerivedCommand(Origin);

/// <summary>Second handler for the library's event, living in this assembly.</summary>
public sealed class ApplicationLibraryEventHandler(LibraryTrace trace) : IEventHandler<LibraryEvent>
{
    public ValueTask Handle(LibraryEvent message, CancellationToken cancellationToken)
    {
        trace.Add($"application:{message.Tag}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Event type with no handler in any assembly.</summary>
public sealed record OrphanEvent : IEvent;

/// <summary>
/// Open generic declared in the application assembly: the library's generator never sees it, so this one closes it over the library's public messages.
/// </summary>
public sealed class ApplicationTraceMiddleware<TMessage, TResponse>(TraceLog sink) :
    IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandle<TMessage, TResponse> next,
        CancellationToken cancellationToken
    )
    {
        sink.Add($"trace:{typeof(TMessage).Name}");
        return next(message, cancellationToken);
    }
}

/// <summary>Stream counterpart of <see cref="ApplicationTraceMiddleware{TMessage, TResponse}" />.</summary>
public sealed class ApplicationStreamTraceMiddleware<TRequest, TResponse>(TraceLog sink) :
    IStreamMiddleware<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    public IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamMessageHandle<TRequest, TResponse> next,
        CancellationToken cancellationToken
    )
    {
        sink.Add($"stream:{typeof(TRequest).Name}");
        return next(request, cancellationToken);
    }
}

/// <summary>Stream constraint fixture: excludes every stream of value-type items, such as the library's internal one.</summary>
public sealed class ReferenceItemStreamMiddleware<TRequest, TResponse> : IStreamMiddleware<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
    where TResponse : class
{
    public IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamMessageHandle<TRequest, TResponse> next,
        CancellationToken cancellationToken
    ) => next(request, cancellationToken);
}

/// <summary>Constraint fixture: library value responses close; library reference responses violate the constraint and skip.</summary>
public sealed class StructResponseMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
    where TMessage : IMessage<TResponse>
    where TResponse : struct
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandle<TMessage, TResponse> next,
        CancellationToken cancellationToken
    ) => next(message, cancellationToken);
}