namespace Sharp.Mediator.LibraryFixtures;

public sealed record LibraryPing(int Value) : IRequest<int>;

public sealed class LibraryPingHandler : IRequestHandler<LibraryPing, int>
{
    public ValueTask<int> Handle(LibraryPing message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(message.Value * 10);
}