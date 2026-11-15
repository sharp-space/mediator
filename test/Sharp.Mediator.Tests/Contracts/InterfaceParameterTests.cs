namespace Sharp.Mediator.Tests.Contracts;

// A caller can name the arguments of an interface method, so the names of its parameters are part of
// the public contract.
public sealed class InterfaceParameterTests
{
    [Theory]
    [InlineData(typeof(IRequestHandler<>))]
    [InlineData(typeof(IRequestHandler<,>))]
    [InlineData(typeof(IEventHandler<>))]
    [InlineData(typeof(IStreamRequestHandler<,>))]
    [InlineData(typeof(IMiddleware<>))]
    [InlineData(typeof(IMiddleware<,>))]
    [InlineData(typeof(IStreamMiddleware<,>))]
    // Every handler and middleware calls what it receives the message; IStreamRequestHandler alone called
    // it the request.
    public void Handle_NamesItsFirstParameterMessage(Type contract)
    {
        var handle = Assert.Single(contract.GetMethods(), static x => x.Name == "Handle");
        Assert.Equal("message", handle.GetParameters()[0].Name);
    }
}