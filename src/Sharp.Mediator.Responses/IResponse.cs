using Sharp.Validator.Result;

namespace Sharp.Mediator.Responses;

/// <summary>Common shape for response envelopes. Lets generic code inspect status, error, and validation errors uniformly.</summary>
public interface IResponse
{
    /// <summary>Result classification for this response.</summary>
    ResponseStatus Status { get; init; }

    /// <summary>
    /// Domain error details when <see cref="Status" /> is <see cref="ResponseStatus.Error" />, <see cref="ResponseStatus.NotFound" />,
    /// <see cref="ResponseStatus.Unauthenticated" />, or <see cref="ResponseStatus.Forbidden" />.
    /// </summary>
    ResponseError? Error { get; init; }

    /// <summary>Validation failures when <see cref="Status" /> is <see cref="ResponseStatus.Invalid" />.</summary>
    IEnumerable<ValidationError> ValidationErrors { get; init; }
}