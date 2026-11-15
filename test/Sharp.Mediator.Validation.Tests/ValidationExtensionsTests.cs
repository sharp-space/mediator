using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Responses;
using Sharp.Mediator.Validation.Tests.Fixtures;
using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation.Tests;

public sealed class ValidationExtensionsTests
{
    private const string TypedModeConflictMessage =
        "AddValidationMiddleware()/AddThrowingResponseValidationMiddleware() and " +
        "AddResponseValidationMiddleware() are mutually exclusive for response-returning requests. " +
        "Choose one typed validation policy.";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TypedValidationPolicies_ConflictInOneBuilder_RegardlessOfOrder(bool responseFirst, bool convenienceRegistration)
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMediator(builder =>
        {
            if (responseFirst) builder.AddResponseValidationMiddleware();

            if (convenienceRegistration) builder.AddValidationMiddleware();
            else builder.AddThrowingResponseValidationMiddleware();

            if (!responseFirst) builder.AddResponseValidationMiddleware();
        }));

        Assert.Equal(TypedModeConflictMessage, exception.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TypedValidationPolicies_ConflictAcrossRepeatedAddMediator_RegardlessOfOrder(bool responseFirst, bool convenienceRegistration)
    {
        var services = new ServiceCollection();
        services.AddMediator(builder =>
        {
            if (responseFirst) builder.AddResponseValidationMiddleware();
            else if (convenienceRegistration) builder.AddValidationMiddleware();
            else builder.AddThrowingResponseValidationMiddleware();
        });

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMediator(builder =>
        {
            if (responseFirst)
            {
                if (convenienceRegistration) builder.AddValidationMiddleware();
                else builder.AddThrowingResponseValidationMiddleware();
            }
            else builder.AddResponseValidationMiddleware();
        }));

        Assert.Equal(TypedModeConflictMessage, exception.Message);
    }

    [Fact]
    public async Task FailedTypedPolicyConflict_DoesNotCorruptPersistedConfiguration()
    {
        var services = new ServiceCollection();
        var validator = new FakeValidator<Command>();
        services.AddSingleton<IValidator<Command>>(validator);
        services.AddMediator(builder => builder
            .AddResponseValidationMiddleware()
            .AddHandler<CommandHandler>());

        Assert.Throws<InvalidOperationException>(() => services.AddMediator(builder =>
            builder.AddThrowingResponseValidationMiddleware()));

        services.AddMediator(builder => builder.AddResponseValidationMiddleware());
        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Request<Command, CommandResponse>(new Command("still-response"));
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Equal("still-response", response.Value);
        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestAndResponseValidation_CoexistInOneBuilder_RegardlessOfOrder(bool responseFirst)
    {
        var services = new ServiceCollection();
        var voidValidator = new FakeValidator<VoidCommand>();
        var responseValidator = new FakeValidator<Command>();

        services.AddSingleton<IValidator<VoidCommand>>(voidValidator);
        services.AddSingleton<IValidator<Command>>(responseValidator);
        services.AddMediator(builder =>
        {
            if (responseFirst)
            {
                builder.AddResponseValidationMiddleware();
                builder.AddRequestValidationMiddleware();
            }
            else
            {
                builder.AddRequestValidationMiddleware();
                builder.AddResponseValidationMiddleware();
            }

            builder.AddHandler<VoidHandler>();
            builder.AddHandler<CommandHandler>();
        });

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Request(new VoidCommand());
        var response = await mediator.Request<Command, CommandResponse>(new Command("combined"));
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Equal("combined", response.Value);
        Assert.Equal(1, voidValidator.AsyncInvocationCount);
        Assert.Equal(1, responseValidator.AsyncInvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestAndResponseValidation_CoexistAcrossRepeatedAddMediator_RegardlessOfOrder(bool responseFirst)
    {
        var services = new ServiceCollection();
        var voidValidator = new FakeValidator<VoidCommand>();
        var responseValidator = new FakeValidator<Command>();

        services.AddSingleton<IValidator<VoidCommand>>(voidValidator);
        services.AddSingleton<IValidator<Command>>(responseValidator);
        services.AddMediator(builder =>
        {
            if (responseFirst) builder.AddResponseValidationMiddleware();
            else builder.AddRequestValidationMiddleware();

            builder.AddHandler<VoidHandler>();
            builder.AddHandler<CommandHandler>();
        });

        services.AddMediator(builder =>
        {
            if (responseFirst) builder.AddRequestValidationMiddleware();
            else builder.AddResponseValidationMiddleware();
        });

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Request(new VoidCommand());
        var response = await mediator.Request<Command, CommandResponse>(new Command("repeated-call"));
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Equal("repeated-call", response.Value);
        Assert.Equal(1, voidValidator.AsyncInvocationCount);
        Assert.Equal(1, responseValidator.AsyncInvocationCount);
    }

    [Fact]
    public async Task RepeatedRegistrations_RequestAndResponseValidation_ValidatesOnce()
    {
        var services = new ServiceCollection();
        var voidValidator = new FakeValidator<VoidCommand>();
        var responseValidator = new FakeValidator<Command>();

        services.AddSingleton<IValidator<VoidCommand>>(voidValidator);
        services.AddSingleton<IValidator<Command>>(responseValidator);
        services.AddMediator(builder => builder
            .AddRequestValidationMiddleware()
            .AddRequestValidationMiddleware()
            .AddResponseValidationMiddleware()
            .AddResponseValidationMiddleware()
            .AddHandler<VoidHandler>()
            .AddHandler<CommandHandler>());

        services.AddMediator(builder => builder
            .AddRequestValidationMiddleware()
            .AddResponseValidationMiddleware());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Request(new VoidCommand());
        var response = await mediator.Request<Command, CommandResponse>(new Command("idempotent"));
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Equal("idempotent", response.Value);
        Assert.Equal(1, voidValidator.AsyncInvocationCount);
        Assert.Equal(1, responseValidator.AsyncInvocationCount);
    }

    [Fact]
    public async Task RepeatedRegistrations_ThrowingTypedValidation_ValidatesOnce()
    {
        var services = new ServiceCollection();
        var validator = new FakeValidator<Command>();
        services.AddSingleton<IValidator<Command>>(validator);

        services.AddMediator(builder => builder
            .AddThrowingResponseValidationMiddleware()
            .AddValidationMiddleware()
            .AddHandler<CommandHandler>());

        services.AddMediator(builder => builder
            .AddThrowingResponseValidationMiddleware());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Request<Command, CommandResponse>(new Command("throw-idempotent"));
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Equal("throw-idempotent", response.Value);
        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestAndThrowingTypedValidation_CoexistRegardlessOfOrder(bool typedFirst)
    {
        var services = new ServiceCollection();
        var voidValidator = new FakeValidator<VoidCommand>();
        var responseValidator = new FakeValidator<Command>();
        services.AddSingleton<IValidator<VoidCommand>>(voidValidator);
        services.AddSingleton<IValidator<Command>>(responseValidator);
        services.AddMediator(builder =>
        {
            if (typedFirst) builder.AddThrowingResponseValidationMiddleware();

            builder.AddRequestValidationMiddleware();

            if (!typedFirst) builder.AddThrowingResponseValidationMiddleware();

            builder.AddHandler<VoidHandler>();
            builder.AddHandler<CommandHandler>();
        });

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Request(new VoidCommand());
        var response = await mediator.Request<Command, CommandResponse>(new Command("throwing-combined"));
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Equal(1, voidValidator.AsyncInvocationCount);
        Assert.Equal(1, responseValidator.AsyncInvocationCount);
    }

    [Fact]
    public async Task RequestAndResponseValidation_KeepTheirDistinctFailureSemantics()
    {
        var services = new ServiceCollection();
        var voidValidator = new FakeValidator<VoidCommand>(new ValidationError("Void", "invalid", "VOID"));
        var responseValidator = new FakeValidator<Command>(new ValidationError("Value", "invalid", "VALUE"));

        services.AddSingleton<IValidator<VoidCommand>>(voidValidator);
        services.AddSingleton<IValidator<Command>>(responseValidator);
        services.AddMediator(builder => builder
            .AddRequestValidationMiddleware()
            .AddResponseValidationMiddleware()
            .AddHandler<VoidHandler>()
            .AddHandler<CommandHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await Assert.ThrowsAsync<ValidationException>(() => mediator.Request(new VoidCommand()).AsTask());

        var response = await mediator.Request<Command, CommandResponse>(new Command("response-invalid"));
        Assert.Equal(ResponseStatus.Invalid, response.Status);
        Assert.Single(response.ValidationErrors);
        Assert.Equal(1, voidValidator.AsyncInvocationCount);
        Assert.Equal(1, responseValidator.AsyncInvocationCount);
    }

    private static IMediator StreamMediator(Action<MediatorBuilder> configure, IValidator<Tail> validator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(validator);
        services.AddMediator(builder =>
        {
            builder.AddHandler<TailHandler>();
            configure(builder);
        });

        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
    }

    private static async Task<List<int>> Drain(IAsyncEnumerable<int> stream)
    {
        List<int> items = [];
        await foreach (var item in stream)
            items.Add(item);

        return items;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamValidation_InvalidRequest_ThrowsBeforeTheFirstItem(bool convenienceRegistration)
    {
        var validator = new FakeValidator<Tail>(new ValidationError("Count", "invalid", "INVALID"));
        var mediator = StreamMediator(
            builder =>
            {
                if (convenienceRegistration) builder.AddValidationMiddleware();
                else builder.AddStreamValidationMiddleware();
            },
            validator);

        await Assert.ThrowsAsync<ValidationException>(() => Drain(mediator.RequestStream<Tail, int>(new Tail(2))));
        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Fact]
    public async Task StreamValidation_ValidRequest_YieldsTheHandlersItems()
    {
        var validator = new FakeValidator<Tail>();
        var mediator = StreamMediator(static builder => builder.AddStreamValidationMiddleware(), validator);
        Assert.Equal([0, 1], await Drain(mediator.RequestStream<Tail, int>(new Tail(2))));
        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Fact]
    public void StreamValidation_ComposesWithResponseReturningPolicy()
    {
        var services = new ServiceCollection();
        services.AddMediator(static builder => builder
            .AddHandler<TailHandler>()
            .AddHandler<CommandHandler>()
            .AddStreamValidationMiddleware()
            .AddResponseValidationMiddleware());

        Assert.Contains(services, static x => x.ServiceType == typeof(IStreamMiddleware<Tail, int>));
        Assert.Contains(services, static x => x.ImplementationType == typeof(ResponseValidationMiddleware<Command, CommandResponse>));
    }

    public sealed record VoidCommand : IRequest;

    public sealed record Command(string Value) : IRequest<CommandResponse>;

    public sealed class VoidHandler : IRequestHandler<VoidCommand>
    {
        public ValueTask Handle(VoidCommand message, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    public sealed class CommandResponse : Response
    {
        public string? Value { get; init; }
    }

    public sealed class CommandHandler : IRequestHandler<Command, CommandResponse>
    {
        public ValueTask<CommandResponse> Handle(Command message, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CommandResponse
            {
                Status = ResponseStatus.Success,
                Value = message.Value
            });
    }

    // ---- stream requests --------------------------------------------------------------------------------------------

    public sealed record Tail(int Count) : IStreamRequest<int>;

    public sealed class TailHandler : IStreamRequestHandler<Tail, int>
    {
        public async IAsyncEnumerable<int> Handle(Tail request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < request.Count; i++)
            {
                yield return i;
                await Task.Yield();
            }
        }
    }
}