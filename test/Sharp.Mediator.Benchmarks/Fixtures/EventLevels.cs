namespace Sharp.Mediator.Benchmarks.Fixtures;

// Events whose handlers are on two levels: one on the event's own type, and one on its base class or on an interface it
// implements. SignalEvent's are both on its own type.
public abstract record BaseSignal : IEvent;

public sealed record DerivedSignal : BaseSignal;

public interface ITaggedSignal : IEvent;

public sealed record TaggedSignal : ITaggedSignal;

public readonly record struct ValueSignal : ITaggedSignal;

public sealed class DerivedSignalHandler : IEventHandler<DerivedSignal>
{
    public ValueTask Handle(DerivedSignal message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class BaseSignalHandler : IEventHandler<BaseSignal>
{
    public ValueTask Handle(BaseSignal message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class TaggedSignalHandler : IEventHandler<TaggedSignal>
{
    public ValueTask Handle(TaggedSignal message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class ValueSignalHandler : IEventHandler<ValueSignal>
{
    public ValueTask Handle(ValueSignal message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

// The interface's level, which TaggedSignal and ValueSignal both reach.
public sealed class TagHandler : IEventHandler<ITaggedSignal>
{
    public ValueTask Handle(ITaggedSignal message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}