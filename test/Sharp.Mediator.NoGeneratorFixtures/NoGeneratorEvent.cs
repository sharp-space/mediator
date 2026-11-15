namespace Sharp.Mediator.NoGeneratorFixtures;

/// <summary>Event whose handler this assembly compiles without the mediator source generator.</summary>
public sealed record NoGeneratorEvent : IEvent;

/// <summary>Used only to verify that a handler no generated code declares is rejected when registered directly.</summary>
public sealed class NoGeneratorEventHandler : IEventHandler<NoGeneratorEvent>
{
    public ValueTask Handle(NoGeneratorEvent message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Event-handler-package stand-in: a public open generic handler in an assembly built without the generator.</summary>
public sealed class PackagedEventHandler<TEvent>(PackagedTrace trace) : IEventHandler<TEvent> where TEvent : IEvent
{
    public ValueTask Handle(TEvent message, CancellationToken cancellationToken)
    {
        trace.Add($"packaged-event:{typeof(TEvent).Name}");
        return ValueTask.CompletedTask;
    }
}