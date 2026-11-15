namespace Sharp.Mediator.NoGeneratorFixtures;

/// <summary>Request whose handler this assembly compiles without the mediator source generator.</summary>
public sealed record NoGeneratorRequest : IRequest<int>;

/// <summary>Used only to verify AddHandlers guidance when no generated registrar exists.</summary>
public sealed class NoGeneratorRequestHandler : IRequestHandler<NoGeneratorRequest, int>
{
    public ValueTask<int> Handle(NoGeneratorRequest message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(1);
}