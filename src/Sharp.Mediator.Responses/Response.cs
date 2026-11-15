using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Responses;

/// <summary>Default valueless response envelope. Use <see cref="Responses" /> factory methods to build instances.</summary>
public class Response : IResponse
{
    /// <summary>Whether the response succeeded.</summary>
    [JsonIgnore]
    public bool IsSuccess => Status == ResponseStatus.Success;

    /// <inheritdoc />
    public ResponseStatus Status { get; init; }

    /// <inheritdoc />
    public ResponseError? Error { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationError> ValidationErrors { get; init; } = [];
}

/// <summary>
/// Response envelope carrying a <typeparamref name="T" /> on success. Every property (including <see cref="Value" />) is marked
/// <see cref="JsonIgnoreAttribute" />, so serializing the envelope directly produces an empty object — it is not a wire shape. Map it with
/// <c>Sharp.Mediator.HttpResults</c> (which writes the bare <see cref="Value" /> on success) or serialize <see cref="Value" /> yourself.
/// <see cref="Page{T}" />, <see cref="CursorPage{T}" /> and <see cref="Identifier{T}" /> are values for the common shapes.
/// </summary>
public sealed class Response<T> : IResponse where T : notnull
{
    private readonly T _value = default!;

    /// <summary>The success value. A response that did not succeed has none, and reading it throws.</summary>
    /// <exception cref="InvalidOperationException">
    /// The response did not succeed. Where it can fail, check <see cref="IsSuccess" /> or use <see cref="TryGetValue" />.
    /// </exception>
    [JsonIgnore]
    public T Value
    {
        get => Status == ResponseStatus.Success ? _value : throw NoValue(Status);
        init => _value = value;
    }

    /// <summary>Whether the response succeeded, and so has a <see cref="Value" />.</summary>
    [JsonIgnore]
    public bool IsSuccess => Status == ResponseStatus.Success;

    /// <inheritdoc />
    [JsonIgnore]
    public ResponseStatus Status { get; init; }

    /// <inheritdoc />
    [JsonIgnore]
    public ResponseError? Error { get; init; }

    /// <inheritdoc />
    [JsonIgnore]
    public IEnumerable<ValidationError> ValidationErrors { get; init; } = [];

    /// <summary>Gets the <see cref="Value" /> of a successful response.</summary>
    /// <returns>
    /// Whether the response succeeded. When it did not, <paramref name="value" /> is <c>default</c>.
    /// </returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        if (Status == ResponseStatus.Success)
        {
            value = _value;
            return true;
        }

        value = default;

        return false;
    }

    /// <summary>Unwraps the response's <see cref="Value" />, which throws unless the response succeeded.</summary>
    public static explicit operator T(Response<T> response) => response.Value;

    /// <summary>Wraps a bare value as a successful response.</summary>
    public static implicit operator Response<T>(T value) => new() { Status = ResponseStatus.Success, Value = value };

    /// <summary>
    /// A failure as a response of this value, with its status, error, and validation errors. Only a failure converts: a successful <see cref="Response" />
    /// has no value to carry. To pass on the failure of another response, use <see cref="ResponseFailure.From" />.
    /// </summary>
    public static implicit operator Response<T>(ResponseFailure failure) => new()
    {
        Status = failure.Status,
        Error = failure.Error,
        ValidationErrors = failure.ValidationErrors
    };

    private static InvalidOperationException NoValue(ResponseStatus status) =>
        new($"A response with status {status} has no value. Check IsSuccess, or use TryGetValue, before reading it.");
}