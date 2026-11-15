namespace Sharp.Mediator.Benchmarks.Fixtures;

public sealed record SignalEvent : IEvent;

// Events of one, two, four and six handlers, for EventHandlerCountBenchmarks. Each handler has a method of its own, as
// in an application, so that the JIT meets a different target for each one a call site calls.
public sealed record Signal1 : IEvent;

public sealed record Signal2 : IEvent;

public sealed record Signal4 : IEvent;

public sealed record Signal6 : IEvent;

public sealed class FirstSignalHandler : IEventHandler<SignalEvent>
{
    public ValueTask Handle(SignalEvent message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class SecondSignalHandler : IEventHandler<SignalEvent>
{
    public ValueTask Handle(SignalEvent message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal1Handler1 : IEventHandler<Signal1>
{
    public ValueTask Handle(Signal1 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal2Handler1 : IEventHandler<Signal2>
{
    public ValueTask Handle(Signal2 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal2Handler2 : IEventHandler<Signal2>
{
    public ValueTask Handle(Signal2 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal4Handler1 : IEventHandler<Signal4>
{
    public ValueTask Handle(Signal4 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal4Handler2 : IEventHandler<Signal4>
{
    public ValueTask Handle(Signal4 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal4Handler3 : IEventHandler<Signal4>
{
    public ValueTask Handle(Signal4 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal4Handler4 : IEventHandler<Signal4>
{
    public ValueTask Handle(Signal4 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal6Handler1 : IEventHandler<Signal6>
{
    public ValueTask Handle(Signal6 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal6Handler2 : IEventHandler<Signal6>
{
    public ValueTask Handle(Signal6 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal6Handler3 : IEventHandler<Signal6>
{
    public ValueTask Handle(Signal6 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal6Handler4 : IEventHandler<Signal6>
{
    public ValueTask Handle(Signal6 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal6Handler5 : IEventHandler<Signal6>
{
    public ValueTask Handle(Signal6 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class Signal6Handler6 : IEventHandler<Signal6>
{
    public ValueTask Handle(Signal6 message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}