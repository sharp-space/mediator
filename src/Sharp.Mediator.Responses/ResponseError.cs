namespace Sharp.Mediator.Responses;

/// <summary>A domain error: stable <paramref name="Code" /> for clients, optional human-readable <paramref name="Message" />.</summary>
public sealed record ResponseError(string Code, string? Message);