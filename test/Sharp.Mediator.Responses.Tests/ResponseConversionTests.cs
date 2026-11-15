using System.Text.Json;

namespace Sharp.Mediator.Responses.Tests;

public sealed class ResponseConversionTests
{
    [Fact]
    public void ImplicitConversion_FromValue_ReturnsSuccessResponse()
    {
        Response<int> response = 7;
        Assert.Equal(7, response.Value);
        Assert.Equal(ResponseStatus.Success, response.Status);
    }

    [Fact]
    public void ExplicitConversion_ResponseToValue_Unwraps()
    {
        // Unwrap is explicit because, like Value, it throws unless the response succeeded (see ResponseValueTests).
        var response = Responses.Success(42);
        var unwrapped = (int)response;
        Assert.Equal(42, unwrapped);
    }

    [Fact]
    public void ImplicitConversion_FromFailure_PreservesStatusAndError()
    {
        var error = Responses.Error("E", "msg");
        Response<int> converted = error;
        Response valueless = error;

        Assert.Equal(ResponseStatus.Error, converted.Status);
        Assert.NotNull(converted.Error);
        Assert.Equal("E", converted.Error.Code);
        Assert.Equal(ResponseStatus.Error, valueless.Status);
    }

    [Fact]
    // Only a failure becomes a response of a value. Response<string> r = Responses.Success() used to compile and make a
    // success whose value was null; a valueless Response no longer converts at all.
    public void ValuelessResponse_DoesNotConvertToAResponseOfAValue()
    {
        var conversions = typeof(Response<string>).GetMethods()
            .Where(static x => x.Name == "op_Implicit")
            .Select(static x => x.GetParameters()[0].ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(Response), conversions);
        Assert.Contains(typeof(ResponseFailure), conversions);
    }

    [Fact]
    public void From_PassesOnTheFailureOfAnotherResponse()
    {
        Response notFound = Responses.NotFound("missing", "no such user");

        Response<int> converted = ResponseFailure.From(notFound);
        Assert.Equal(ResponseStatus.NotFound, converted.Status);
        Assert.Equal(new ResponseError("missing", "no such user"), converted.Error);
    }

    [Fact]
    public void From_RejectsAResponseThatIsNotAFailure()
    {
        Assert.Throws<ArgumentException>(static () => ResponseFailure.From(Responses.Success()));
        Assert.Throws<ArgumentException>(static () => ResponseFailure.From(new Response()));
        Assert.Throws<ArgumentNullException>(static () => ResponseFailure.From(null!));
    }

    [Fact]
    // A page, a cursor page and an identifier are values like any other: a handler returns one, and it becomes
    // a successful response.
    public void ImplicitConversion_FromAPayload_IsASuccess()
    {
        Response<Page<int>> page = new Page<int>([1, 2, 3], 3);
        Response<CursorPage<int>> cursor = new CursorPage<int>([9, 8], "next");
        Response<Identifier<int>> id = new Identifier<int>(99);

        Assert.Equal([1, 2, 3], page.Value.Items);
        Assert.Equal("next", cursor.Value.NextCursor);
        Assert.Equal(99, id.Value.Id);
        Assert.All([page.Status, cursor.Status, id.Status], static x => Assert.Equal(ResponseStatus.Success, x));
    }

    [Fact]
    public void ImplicitConversion_FromFailureToPayloadResponse_PreservesStatusAndErrors()
    {
        Response<Identifier<int>> error = Responses.Error("E", "msg");
        Response<Page<int>> notFound = Responses.NotFound("missing");
        Response<CursorPage<int>> invalid = Responses.Invalid("Name", "required");

        Assert.Equal(ResponseStatus.Error, error.Status);
        Assert.Equal("E", error.Error!.Code);
        Assert.Equal(ResponseStatus.NotFound, notFound.Status);
        Assert.Equal("missing", notFound.Error!.Code);
        Assert.Equal(ResponseStatus.Invalid, invalid.Status);
        Assert.Single(invalid.ValidationErrors);
    }

    [Fact]
    // The envelope is not a wire shape: every property of Response<T> is [JsonIgnore]. Its value is, and the payloads
    // take their names from the serializer's options, as the application's own types do.
    public void Serialization_WritesPayloadsWithSerializerNaming_NotTheEnvelope()
    {
        Assert.Equal("{}", JsonSerializer.Serialize(Responses.Id<long>(123)));
        Assert.Equal("{\"id\":123}", JsonSerializer.Serialize(new Identifier<long>(123), JsonSerializerOptions.Web));

        Assert.Equal(
            "{\"items\":[1,2],\"count\":10}", JsonSerializer.Serialize(new Page<int>([1, 2], 10), JsonSerializerOptions.Web));

        Assert.Equal(
            "{\"items\":[\"a\",\"b\"],\"nextCursor\":\"c\"}",
            JsonSerializer.Serialize(new CursorPage<string>(["a", "b"], "c"), JsonSerializerOptions.Web));
    }
}