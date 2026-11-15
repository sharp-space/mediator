using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Responses;
using Sharp.Validator.Result;
using Factory = Sharp.Mediator.Responses.Responses;

namespace Sharp.Mediator.HttpResults.Tests;

public sealed class HttpResultExtensionsTests
{
    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging()
        .BuildServiceProvider();

    private static async Task<(int StatusCode, string Body)> Execute(IResult result, IServiceProvider? services = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services ?? Services };

        await using var stream = new MemoryStream();
        httpContext.Response.Body = stream;

        await result.ExecuteAsync(httpContext);
        stream.Position = 0;

        using var reader = new StreamReader(stream);
        var body = await reader.ReadToEndAsync();
        return (httpContext.Response.StatusCode, body);
    }

    [Fact]
    public async Task SuccessResponse_MapsToOk_NoBody()
    {
        IResponse response = Factory.Success();
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task ErrorResponse_MapsToBadRequest_WithErrorBody()
    {
        IResponse response = Factory.Error("E1", "bad");
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status400BadRequest, status);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("E1", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("bad", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    // The bodies are written with the application's JSON options, as any Minimal API result is, so the names and the
    // null fields the README shows are those of ASP.NET Core's defaults. An application that configures its own naming
    // policy or ignore condition gets them in these bodies too.
    public async Task ErrorBodies_FollowTheApplicationsJsonOptions()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(static x =>
            {
                x.SerializerOptions.PropertyNamingPolicy = null;
                x.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            })
            .BuildServiceProvider();

        var (_, invalid) = await Execute(Factory.Invalid("Name", "required").ToResult(), services);
        var (_, error) = await Execute(Factory.Error("E1").ToResult(), services);

        Assert.Equal("""{"Errors":[{"PropertyName":"Name","Code":"required","Severity":"Error"}]}""", invalid);
        Assert.Equal("""{"Code":"E1"}""", error);
    }

    [Fact]
    public async Task InvalidResponse_MapsToUnprocessableEntity_WithValidationErrors()
    {
        IResponse response = Factory.Invalid("Name", "required", "Name is required");
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);

        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.GetProperty("errors")[0];
        Assert.Equal("Name", first.GetProperty("propertyName").GetString());
        Assert.Equal("Name is required", first.GetProperty("message").GetString());
        Assert.Equal("required", first.GetProperty("code").GetString());
        Assert.Equal("Error", first.GetProperty("severity").GetString());
    }

    [Fact]
    // The 422 body is the HTTP contract: an object holding nothing but the entries, in the response's order, each with
    // exactly these four fields, whatever Sharp.Validator's ValidationError gains.
    public async Task InvalidResponse_Body_HasExactlyTheErrorsWithTheirFourFields()
    {
        IResponse response = Factory.Invalid(
            new ValidationError("Email", "'Email' is not a valid email address.", "EmailAddress"),
            new ValidationError("Nickname", null, null, ValidationErrorSeverity.Warning));

        var (_, body) = await Execute(response.ToResult());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["errors"], doc.RootElement.EnumerateObject().Select(static x => x.Name));

        var entries = doc.RootElement.GetProperty("errors").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);

        Assert.All(entries,
            static x => Assert.Equal(["propertyName", "message", "code", "severity"],
                x.EnumerateObject().Select(static y => y.Name)));

        Assert.Equal("Email", entries[0].GetProperty("propertyName").GetString());
        Assert.Equal("Nickname", entries[1].GetProperty("propertyName").GetString());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("message").ValueKind);
        Assert.Equal("Warning", entries[1].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task NotFoundResponse_NoError_MapsTo404NoBody()
    {
        IResponse response = Factory.NotFound();
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task NotFoundResponse_WithError_MapsTo404WithBody()
    {
        IResponse response = Factory.NotFound("missing", "user not found");
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Contains("\"code\":\"missing\"", body);
    }

    [Fact]
    public async Task UnauthenticatedResponse_NoError_MapsTo401NoBody()
    {
        IResponse response = Factory.Unauthenticated();
        var (status, _) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task UnauthenticatedResponse_WithError_MapsTo401WithBody()
    {
        IResponse response = Factory.Unauthenticated("AUTH", "login required");
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Contains("\"code\":\"AUTH\"", body);
    }

    [Fact]
    public async Task ForbiddenResponse_WithError_MapsTo403WithBody()
    {
        IResponse response = Factory.Forbidden("DENY", "forbidden");
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("\"code\":\"DENY\"", body);
    }

    [Fact]
    public async Task ResponseT_Success_MapsToOkWithValue()
    {
        var response = Factory.Success(42);
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("42", body);
    }

    [Fact]
    public async Task ResponseT_NonSuccess_DelegatesToInterfaceMapping()
    {
        Response<int> errorPath = Factory.Error("X", "x");
        var (status, _) = await Execute(errorPath.ToResult());
        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task IdentifierResponse_Success_EmitsIdOnly()
    {
        var response = Factory.Id<long>(123);
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(123, doc.RootElement.GetProperty("id").GetInt64());
        Assert.False(doc.RootElement.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task ResponseTypedAsIResponse_SelectsStatusOnlyOverload()
    {
        IResponse response = Factory.Id<long>(123);
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task PageResponse_Success_EmitsItemsAndCount()
    {
        var response = Factory.Page([1, 2, 3], 99);
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(99, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task CursorResponse_Success_EmitsItemsAndNextCursor()
    {
        var response = Factory.Cursor<string>(["a", "b"], "tok");
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("tok", doc.RootElement.GetProperty("nextCursor").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ValueTaskResponse_Awaited_BeforeConversion()
    {
        ValueTask<Response> task = new(Factory.Success());
        var (status, _) = await Execute(await task.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task ValueTaskResponseT_Awaited_BeforeConversion()
    {
        ValueTask<Response<int>> task = new(Factory.Success(11));
        var (status, body) = await Execute(await task.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("11", body);
    }

    [Fact]
    public async Task ValueTaskIdentifierResponse_Awaited_BeforeConversion()
    {
        ValueTask<Response<Identifier<long>>> task = new(Factory.Id<long>(8));
        var (status, body) = await Execute(await task.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"id\":8", body);
    }

    [Fact]
    public async Task ValueTaskPageResponse_Awaited_BeforeConversion()
    {
        ValueTask<Response<Page<int>>> task = new(Factory.Page([1, 2, 3], 3));
        var (status, body) = await Execute(await task.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"count\":3", body);
    }

    [Fact]
    public async Task ValueTaskCursorResponse_Awaited_BeforeConversion()
    {
        ValueTask<Response<CursorPage<string>>> task = new(Factory.Cursor<string>(["a", "b"], "c"));
        var (status, body) = await Execute(await task.ToResult());
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"nextCursor\":\"c\"", body);
    }

    [Fact]
    public async Task IdentifierResponse_NonSuccess_DelegatesToInterfaceMapping()
    {
        Response<Identifier<long>> errorPath = Factory.Error("X", "x");
        var (status, _) = await Execute(errorPath.ToResult());
        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task PageResponse_NonSuccess_DelegatesToInterfaceMapping()
    {
        Response<Page<int>> notFound = Factory.NotFound();
        var (status, _) = await Execute(notFound.ToResult());
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    [Fact]
    public async Task CursorResponse_NonSuccess_DelegatesToInterfaceMapping()
    {
        Response<CursorPage<int>> invalid = Factory.Invalid("Name", "required");
        var (status, _) = await Execute(invalid.ToResult());
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
    }

    [Fact]
    public async Task ErrorResponse_WithoutErrorDetails_MapsToBadRequestWithoutBody()
    {
        IResponse response = Factory.Error();
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task ForbiddenResponse_NoError_MapsToPlain403WithoutBody()
    {
        IResponse response = Factory.Forbidden();
        var (status, body) = await Execute(response.ToResult());
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Empty(body);
    }

    [Fact]
    public void UnspecifiedResponse_ThrowsClearInvalidOperationException()
    {
        IResponse response = new Response();
        var exception = Assert.Throws<InvalidOperationException>(response.ToResult);
        Assert.Contains(nameof(ResponseStatus.Unspecified), exception.Message);
        Assert.Contains("Responses factory", exception.Message);
    }

    [Fact]
    public void UnknownStatus_Throws_NotSupportedException()
    {
        var weird = new WeirdResponse();
        Assert.Throws<NotSupportedException>(weird.ToResult);
    }

    private sealed class WeirdResponse : IResponse
    {
        public ResponseStatus Status { get; init; } = (ResponseStatus)999;

        public ResponseError? Error { get; init; }

        public IEnumerable<ValidationError> ValidationErrors { get; init; } = [];
    }
}