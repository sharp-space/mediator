namespace Sharp.Mediator.Responses.Tests;

public sealed class ResponseInterfaceTests
{
    // Generic code that knows a response type only as IResponse, new() builds it through the interface's init
    // accessors, as ResponseValidationMiddleware does for invalid input. Every response with a public constructor must
    // keep what they set, or such code returns a response without the error it was given.
    [Fact]
    public void Response_CreatedThroughTheInterface_KeepsStatusAndError() => AssertKeepsStatusAndError<Response>();

    [Fact]
    public void ValueResponse_CreatedThroughTheInterface_KeepsStatusAndError() => AssertKeepsStatusAndError<Response<string>>();

    private static void AssertKeepsStatusAndError<TResponse>() where TResponse : IResponse, new()
    {
        var error = new ResponseError("missing", "No such order.");

        IResponse response = new TResponse { Status = ResponseStatus.NotFound, Error = error };
        Assert.Equal(ResponseStatus.NotFound, response.Status);
        Assert.Same(error, response.Error);
    }
}