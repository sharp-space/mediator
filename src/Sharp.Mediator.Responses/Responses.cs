using Sharp.Validator.Result;

namespace Sharp.Mediator.Responses;

/// <summary>Factory helpers for building response envelopes.</summary>
public static class Responses
{
    /// <summary>Successful response with no value.</summary>
    public static Response Success() => new() { Status = ResponseStatus.Success };

    /// <summary>Successful response carrying <paramref name="value" />.</summary>
    public static Response<TValue> Success<TValue>(TValue value) where TValue : notnull =>
        new() { Status = ResponseStatus.Success, Value = value };

    /// <summary>Generic error response with no details.</summary>
    public static ResponseFailure Error() => new(ResponseStatus.Error, null, []);

    /// <summary>Error response with a stable <paramref name="code" /> and optional <paramref name="message" />.</summary>
    public static ResponseFailure Error(string code, string? message = null) => new(ResponseStatus.Error, new(code, message), []);

    /// <summary>Invalid response with a single field-level <see cref="ValidationError" />.</summary>
    public static ResponseFailure Invalid(string propertyName, string code, string? message = null) =>
        new(ResponseStatus.Invalid, null, [new(propertyName, message, code)]);

    /// <summary>Invalid response with one or more validation errors.</summary>
    public static ResponseFailure Invalid(params IEnumerable<ValidationError> validationErrors) =>
        new(ResponseStatus.Invalid, null, [.. validationErrors]);

    /// <summary>Not-found response with no details.</summary>
    public static ResponseFailure NotFound() => new(ResponseStatus.NotFound, null, []);

    /// <summary>Not-found response with a stable <paramref name="code" /> and optional <paramref name="message" />.</summary>
    public static ResponseFailure NotFound(string code, string? message = null) =>
        new(ResponseStatus.NotFound, new(code, message), []);

    /// <summary>Unauthenticated (HTTP 401) response with no details.</summary>
    public static ResponseFailure Unauthenticated() => new(ResponseStatus.Unauthenticated, null, []);

    /// <summary>Unauthenticated (HTTP 401) response with a stable <paramref name="code" /> and optional <paramref name="message" />.</summary>
    public static ResponseFailure Unauthenticated(string code, string? message = null) =>
        new(ResponseStatus.Unauthenticated, new(code, message), []);

    /// <summary>Forbidden (HTTP 403) response with no details.</summary>
    public static ResponseFailure Forbidden() => new(ResponseStatus.Forbidden, null, []);

    /// <summary>Forbidden (HTTP 403) response with a stable <paramref name="code" /> and optional <paramref name="message" />.</summary>
    public static ResponseFailure Forbidden(string code, string? message = null) =>
        new(ResponseStatus.Forbidden, new(code, message), []);

    /// <summary>A successful response with the id of what the operation created or found.</summary>
    public static Response<Identifier<T>> Id<T>(T id) where T : notnull => Success(new Identifier<T>(id));

    /// <summary>A successful response with a page of items and, when known, the number across every page.</summary>
    public static Response<Page<T>> Page<T>(IReadOnlyList<T> items, long? count = null) => Success(new Page<T>(items, count));

    /// <summary>A successful response with a page of items and the cursor of the page after it.</summary>
    public static Response<CursorPage<T>> Cursor<T>(IReadOnlyList<T> items, string? nextCursor = null) =>
        Success(new CursorPage<T>(items, nextCursor));
}