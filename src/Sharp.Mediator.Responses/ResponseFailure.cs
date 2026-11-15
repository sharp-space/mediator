using Sharp.Validator.Result;

namespace Sharp.Mediator.Responses;

/// <summary>
/// A response that did not succeed, as the <see cref="Responses" /> factories for errors, invalid input, a missing resource and auth failures return it.
/// It becomes a <see cref="Response" />, or a <see cref="Response{T}" /> of any value, where it is returned. A successful <see cref="Response" /> has no
/// value to carry, so it does not become a <see cref="Response{T}" />.
/// </summary>
public sealed class ResponseFailure : IResponse
{
    internal ResponseFailure(ResponseStatus status, ResponseError? error, IEnumerable<ValidationError> validationErrors)
    {
        Status = status;
        Error = error;
        ValidationErrors = validationErrors;
    }

    /// <inheritdoc />
    public ResponseStatus Status { get; init; }

    /// <inheritdoc />
    public ResponseError? Error { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationError> ValidationErrors { get; init; }

    /// <summary>The failure <paramref name="response" /> holds, to return it as a response of another value.</summary>
    /// <exception cref="ArgumentException">The response succeeded, or its status was never set.</exception>
    public static ResponseFailure From(IResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Status is ResponseStatus.Success or ResponseStatus.Unspecified
            ? throw new ArgumentException(
                $"A response with status {response.Status} is not a failure. A successful response has no value to pass " +
                "on: return the value itself.",
                nameof(response))
            : new(response.Status, response.Error, response.ValidationErrors);
    }

    /// <summary>The failure as a valueless <see cref="Response" />.</summary>
    public static implicit operator Response(ResponseFailure failure) =>
        new() { Status = failure.Status, Error = failure.Error, ValidationErrors = failure.ValidationErrors };
}