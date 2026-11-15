using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Responses;
using Sharp.Mediator.Validation.Tests.Fixtures;
using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation.Tests;

public sealed class ResponseValidationMiddlewareTests
{
    [Fact]
    public async Task NoValidators_HandlerExecutes_Normally()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .AddResponseValidationMiddleware()
            .AddHandler<UserHandler>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Request<CreateUserCommand, UserResponse>(new CreateUserCommand("alice"));
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Equal("u-alice", response.UserId);
    }

    [Fact]
    public async Task ValidatorWithErrors_ReturnsInvalidResponse_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .AddResponseValidationMiddleware()
            .AddHandler<UserHandler>());

        services.AddSingleton<IValidator<CreateUserCommand>>(new FakeValidator<CreateUserCommand>(
            new ValidationError("Name", "too short", "TOO_SHORT")));

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Request<CreateUserCommand, UserResponse>(new CreateUserCommand("a"));
        Assert.Equal(ResponseStatus.Invalid, response.Status);
        Assert.Null(response.UserId);
        var error = Assert.Single(response.ValidationErrors);
        Assert.Equal("Name", error.PropertyName);
        Assert.Equal("TOO_SHORT", error.Code);
    }

    [Fact]
    public async Task ValidatorWithErrors_SkipsHandler()
    {
        var executed = false;
        var services = new ServiceCollection();

        services.AddSingleton<IRequestHandler<CreateUserCommand, UserResponse>>(
            new DelegatingHandler(() => { executed = true; }));

        services.AddMediator(x => x.AddResponseValidationMiddleware());
        services.AddSingleton<IValidator<CreateUserCommand>>(new FakeValidator<CreateUserCommand>(
            new ValidationError("Name", "x", "X")));

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Request<CreateUserCommand, UserResponse>(new CreateUserCommand("a"));

        Assert.False(executed);
        Assert.Equal(ResponseStatus.Invalid, response.Status);
    }

    [Fact]
    public async Task GeneralTypedResponse_IsOutsideResponseValidationContract()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .AddResponseValidationMiddleware()
            .AddHandler<PlainHandler>());

        services.AddSingleton<IValidator<PlainCommand>>(new FakeValidator<PlainCommand>(
            new ValidationError("Value", "invalid", "INVALID")));

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Request<PlainCommand, int>(new PlainCommand());
        Assert.Equal(7, response);
    }

    [Fact]
    public async Task RequestAndResponseValidationCombination_UsesThrowingContractForVoidRequest()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .AddRequestValidationMiddleware()
            .AddResponseValidationMiddleware()
            .AddHandler<VoidHandler>());

        services.AddSingleton<IValidator<VoidCommand>>(new FakeValidator<VoidCommand>(
            new ValidationError("Value", "invalid", "INVALID")));

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        await Assert.ThrowsAsync<ValidationException>(() =>
            mediator.Request(new VoidCommand()).AsTask());
    }

    public sealed record CreateUserCommand(string Name) : IRequest<UserResponse>;

    public sealed class UserResponse : Response
    {
        public string? UserId { get; init; }
    }

    public sealed class UserHandler : IRequestHandler<CreateUserCommand, UserResponse>
    {
        public ValueTask<UserResponse> Handle(CreateUserCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(new UserResponse
        {
            Status = ResponseStatus.Success,
            UserId = "u-" + message.Name
        });
    }

    public sealed record PlainCommand : IRequest<int>;

    public sealed class PlainHandler : IRequestHandler<PlainCommand, int>
    {
        public ValueTask<int> Handle(PlainCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(7);
    }

    public sealed record VoidCommand : IRequest;

    public sealed class VoidHandler : IRequestHandler<VoidCommand>
    {
        public ValueTask Handle(VoidCommand message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class DelegatingHandler(Action onHandle) : IRequestHandler<CreateUserCommand, UserResponse>
    {
        public ValueTask<UserResponse> Handle(CreateUserCommand message, CancellationToken cancellationToken)
        {
            onHandle();
            return ValueTask.FromResult(new UserResponse());
        }
    }
}