namespace Sharp.Mediator.Responses.Tests;

public sealed class ResponseValueTests
{
    [Fact]
    // A response that did not succeed used to return default from Value: null for a reference type, which the type says
    // it never is, and zero for a number, which reads as a real value. Either surfaced later, away from the read that
    // caused it. Only a success has a value, so reading any other response's is the caller's bug and throws there.
    public void Value_OfAFailure_Throws()
    {
        Response<string> notFound = Responses.NotFound("missing");
        Response<int> invalid = Responses.Invalid("Name", "required");

        var exception = Assert.Throws<InvalidOperationException>(() => notFound.Value);
        Assert.Contains(nameof(ResponseStatus.NotFound), exception.Message);
        Assert.Throws<InvalidOperationException>(() => invalid.Value);
        Assert.Throws<InvalidOperationException>(() => (int)invalid);
    }

    [Fact]
    // A response constructed directly stays Unspecified until its status is set, so it is not a success either.
    public void Value_OfAnUnspecifiedResponse_Throws()
    {
        var response = new Response<string> { Value = "set" };
        Assert.Throws<InvalidOperationException>(() => response.Value);
    }

    [Fact]
    public void TryGetValue_GivesTheValueOfASuccessOnly()
    {
        Response<string> success = "found";
        Response<string> failure = Responses.Error("E");

        Assert.True(success.TryGetValue(out var value));
        Assert.Equal("found", value);
        Assert.False(failure.TryGetValue(out var none));
        Assert.Null(none);
    }

    [Fact]
    public void IsSuccess_IsTrueForASuccessOnly()
    {
        Response<int> success = 1;
        Response<int> failure = Responses.Forbidden();
        Response valuelessFailure = Responses.NotFound();

        Assert.True(success.IsSuccess);
        Assert.False(failure.IsSuccess);
        Assert.False(new Response<int>().IsSuccess);
        Assert.True(Responses.Success().IsSuccess);
        Assert.False(valuelessFailure.IsSuccess);
        Assert.False(new Response().IsSuccess);
    }
}