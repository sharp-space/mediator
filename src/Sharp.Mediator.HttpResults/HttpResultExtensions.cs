using Microsoft.AspNetCore.Http;
using Sharp.Mediator.Responses;

namespace Sharp.Mediator.HttpResults;

/// <summary>
/// Maps <see cref="IResponse" />-derived envelopes to ASP.NET Core <see cref="IResult" /> for use in Minimal APIs. Status mapping: Success→200,
/// Error→400, Invalid→422, NotFound→404, Unauthenticated→401, Forbidden→403.
/// </summary>
/// <remarks>
/// A <see cref="Response{T}" /> writes its value on success, whatever that value is: a <see cref="Page{T}" />, a <see cref="CursorPage{T}" />, an
/// <see cref="Identifier{T}" />, or a type of the application's. Typed as <see cref="IResponse" />, which has no value, a response selects the
/// status-only overload. Responses with <see cref="ResponseStatus.Unspecified" /> are rejected. Error/auth/not-found responses without
/// <see cref="IResponse.Error" /> produce an empty body; bare Forbidden produces a plain 403 without invoking an authentication scheme.
/// </remarks>
public static class HttpResultExtensions
{
    /// <summary>Maps an <see cref="IResponse" /> envelope to a status-code-bearing <see cref="IResult" />.</summary>
    public static IResult ToResult(this IResponse response) => response.Status switch
    {
        ResponseStatus.Unspecified => throw new InvalidOperationException(
            "The response status is Unspecified. Create responses through the Responses factory or assign Status explicitly."),
        ResponseStatus.Success => Results.Ok(),
        ResponseStatus.Error => Error(response),
        ResponseStatus.Invalid => Invalid(response),
        ResponseStatus.NotFound => NotFound(response),
        ResponseStatus.Unauthenticated => Unauthenticated(response),
        ResponseStatus.Forbidden => Forbidden(response),
        _ => throw new NotSupportedException($"Result {response.Status} conversion is not supported.")
    };

    /// <summary>
    /// Maps a <see cref="Response{T}" />: on success returns <c>200 OK</c> with the value; otherwise delegates to <see cref="ToResult(IResponse)" />.
    /// </summary>
    public static IResult ToResult<T>(this Response<T> response) where T : notnull => response.Status switch
    {
        ResponseStatus.Success => Results.Ok(response.Value),
        _ => ((IResponse)response).ToResult()
    };

    /// <summary>Awaits the value-task and maps the resulting <see cref="Response" />.</summary>
    public static async ValueTask<IResult> ToResult(this ValueTask<Response> task) => (await task.ConfigureAwait(false)).ToResult();

    /// <summary>Awaits the value-task and maps the resulting <see cref="Response{T}" />.</summary>
    public static async ValueTask<IResult> ToResult<T>(this ValueTask<Response<T>> task) where T : notnull =>
        (await task.ConfigureAwait(false)).ToResult();

    // Error bodies serialize the public ResponseError record directly, as {code, message}. A public record is a
    // stable contract that consumers can register in a JsonSerializerContext, so error bodies do not keep an
    // application from source-generated JSON.
    private static IResult Error(IResponse response) =>
        Results.BadRequest(response.Error);

    private static IResult Invalid(IResponse response) =>
        Results.UnprocessableEntity(new HttpValidationErrors(
        [
            .. response.ValidationErrors.Select(static x =>
                new HttpValidationError(x.PropertyName, x.Message, x.Code, x.Severity.ToString()))
        ]));

    private static IResult NotFound(IResponse response) => response.Error == null
        ? Results.NotFound()
        : Results.NotFound(response.Error);

    private static IResult Unauthenticated(IResponse response) => response.Error == null
        ? Results.Unauthorized()
        : Results.Json(response.Error, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(IResponse response) => response.Error == null
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Json(response.Error, statusCode: StatusCodes.Status403Forbidden);
}