namespace Sharp.Mediator.LibraryFixtures;

/// <summary>Non-sealed so the consuming test assembly can derive commands from it.</summary>
public record BaseCommand(string Origin) : IRequest<string>;

public sealed class BaseCommandHandler : IRequestHandler<BaseCommand, string>
{
    public ValueTask<string> Handle(BaseCommand message, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"base:{message.Origin}");
}