namespace Sharp.Mediator.LibraryFixtures;

public sealed record LibraryEvent(string Tag) : IEvent;

public sealed class LibraryEventHandler(LibraryTrace trace) : IEventHandler<LibraryEvent>
{
    public ValueTask Handle(LibraryEvent message, CancellationToken cancellationToken)
    {
        trace.Add($"library:{message.Tag}");
        return ValueTask.CompletedTask;
    }
}