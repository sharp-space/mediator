namespace Sharp.Mediator.LibraryFixtures;

public sealed record LibraryVoid : IRequest;

public sealed class LibraryVoidHandler(LibraryTrace trace) : IRequestHandler<LibraryVoid>
{
    public ValueTask Handle(LibraryVoid message, CancellationToken cancellationToken)
    {
        trace.Add("library:void");
        return ValueTask.CompletedTask;
    }
}