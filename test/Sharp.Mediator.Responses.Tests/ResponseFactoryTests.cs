using Sharp.Validator.Result;

namespace Sharp.Mediator.Responses.Tests;

public sealed class ResponseFactoryTests
{
    [Fact]
    public void DirectlyConstructedResponse_DefaultsToUnspecified()
    {
        var response = new Response();
        Assert.Equal(ResponseStatus.Unspecified, response.Status);
        Assert.Equal(0, (int)response.Status);
        Assert.Equal(1, (int)ResponseStatus.Success);
        Assert.Equal(6, (int)ResponseStatus.Forbidden);
    }

    [Fact]
    public void Success_DefaultsToSuccessStatus()
    {
        var response = Responses.Success();
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Null(response.Error);
        Assert.Empty(response.ValidationErrors);
    }

    [Fact]
    public void SuccessOfT_WrapsValue()
    {
        var response = Responses.Success(42);
        Assert.Equal(42, response.Value);
        Assert.Equal(ResponseStatus.Success, response.Status);
    }

    [Fact]
    public void Error_NoArgs_HasErrorStatusAndNoErrorDetails()
    {
        var response = Responses.Error();
        Assert.Equal(ResponseStatus.Error, response.Status);
        Assert.Null(response.Error);
    }

    [Fact]
    public void Error_WithCodeAndMessage_PopulatesError()
    {
        var response = Responses.Error("E1", "boom");
        Assert.Equal(ResponseStatus.Error, response.Status);
        Assert.NotNull(response.Error);
        Assert.Equal("E1", response.Error.Code);
        Assert.Equal("boom", response.Error.Message);
    }

    [Fact]
    public void Invalid_SingleField_CreatesValidationError()
    {
        var response = Responses.Invalid("Name", "required", "Name is required");
        Assert.Equal(ResponseStatus.Invalid, response.Status);
        var error = Assert.Single(response.ValidationErrors);
        Assert.Equal("Name", error.PropertyName);
        Assert.Equal("required", error.Code);
        Assert.Equal("Name is required", error.Message);
    }

    [Fact]
    public void Invalid_WithMultipleErrors_PreservesAll()
    {
        var errors = new[]
        {
            new ValidationError("A", "msgA", "codeA"),
            new ValidationError("B", "msgB", "codeB")
        };

        var response = Responses.Invalid(errors);
        Assert.Equal(ResponseStatus.Invalid, response.Status);
        Assert.Equal(2, response.ValidationErrors.Count());
    }

    [Fact]
    public void NotFound_NoArgs_SetsNotFoundStatus()
    {
        var response = Responses.NotFound();
        Assert.Equal(ResponseStatus.NotFound, response.Status);
    }

    [Fact]
    public void Unauthenticated_WithDetails_PopulatesError()
    {
        var response = Responses.Unauthenticated("E_UNAUTH", "login required");
        Assert.Equal(ResponseStatus.Unauthenticated, response.Status);
        Assert.NotNull(response.Error);
        Assert.Equal("E_UNAUTH", response.Error.Code);
    }

    [Fact]
    public void Forbidden_WithDetails_PopulatesError()
    {
        var response = Responses.Forbidden("E_FORBIDDEN", "no access");
        Assert.Equal(ResponseStatus.Forbidden, response.Status);
        Assert.NotNull(response.Error);
        Assert.Equal("E_FORBIDDEN", response.Error.Code);
    }

    [Fact]
    public void Forbidden_NoArgs_HasForbiddenStatusAndNoErrorDetails()
    {
        var response = Responses.Forbidden();
        Assert.Equal(ResponseStatus.Forbidden, response.Status);
        Assert.Null(response.Error);
    }

    [Fact]
    public void Unauthenticated_NoArgs_HasUnauthenticatedStatusAndNoErrorDetails()
    {
        var response = Responses.Unauthenticated();
        Assert.Equal(ResponseStatus.Unauthenticated, response.Status);
        Assert.Null(response.Error);
    }

    [Fact]
    public void NotFound_WithDetails_PopulatesError()
    {
        var response = Responses.NotFound("missing", "not here");
        Assert.Equal(ResponseStatus.NotFound, response.Status);
        Assert.Equal("missing", response.Error!.Code);
    }

    [Fact]
    public void Invalid_SingleFieldWithoutMessage_CreatesValidationError()
    {
        var response = Responses.Invalid("Name", "required");
        var error = Assert.Single(response.ValidationErrors);
        Assert.Equal("Name", error.PropertyName);
        Assert.Equal("required", error.Code);
    }

    [Fact]
    public void Id_FactoryReturnsASuccessWithTheIdentifier()
    {
        var response = Responses.Id(7);
        Assert.Equal(new Identifier<int>(7), response.Value);
        Assert.Equal(ResponseStatus.Success, response.Status);
    }

    [Fact]
    public void Page_FactoryWrapsItemsAndCount()
    {
        var items = new[] { "a", "b" };
        var response = Responses.Page(items, 5);
        Assert.Equal(items, response.Value.Items);
        Assert.Equal(5, response.Value.Count);
        Assert.Equal(ResponseStatus.Success, response.Status);
    }

    [Fact]
    public void Cursor_FactoryWrapsItemsAndNextCursor()
    {
        var items = new[] { 1, 2 };
        var response = Responses.Cursor(items, "next");
        Assert.Equal(items, response.Value.Items);
        Assert.Equal("next", response.Value.NextCursor);
        Assert.Equal(ResponseStatus.Success, response.Status);
    }

    [Fact]
    public void Invalid_MaterializesADeferredSequence()
    {
        // A deferred query must not rerun, or outlive its source, each time the response is read.
        var enumerations = 0;

        IEnumerable<ValidationError> Deferred()
        {
            enumerations++;
            yield return new ValidationError("Name", "required", "REQUIRED");
        }

        var response = Responses.Invalid(Deferred());
        Assert.Single(response.ValidationErrors);
        Assert.Single(response.ValidationErrors);
        Assert.Equal(1, enumerations);
    }
}