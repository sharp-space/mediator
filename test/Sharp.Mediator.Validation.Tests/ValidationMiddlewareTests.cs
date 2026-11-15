using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Validation.Tests.Fixtures;
using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation.Tests;

public sealed class ValidationMiddlewareTests
{
    [Fact]
    public async Task NoValidators_FlowsThroughToHandler_VoidRequest()
    {
        var services = new ServiceCollection();
        services.AddSingleton<VoidCounter>();
        services.AddMediator(x => x
            .AddValidationMiddleware()
            .AddHandler<VoidHandler>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Request(new VoidCommand());

        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<VoidCounter>().Count);
    }

    [Fact]
    public async Task NoValidators_FlowsThroughToHandler_RequestWithResponse()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .AddValidationMiddleware()
            .AddHandler<EchoHandler>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Request<EchoCommand, string>(new EchoCommand("hi"));
        Assert.Equal("hi", result);
    }

    [Fact]
    public async Task ValidatorWithErrors_Throws_ValidationException()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x
            .AddValidationMiddleware()
            .AddHandler<EchoHandler>());

        services.AddSingleton<IValidator<EchoCommand>>(new FakeValidator<EchoCommand>(
            new ValidationError("Name", "is bad", "BAD")));

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            mediator.Request<EchoCommand, string>(new EchoCommand("x")).AsTask());

        var error = Assert.Single(exception.Errors);
        Assert.Equal("Name", error.PropertyName);
        Assert.Equal("BAD", error.Code);
    }

    [Fact]
    public async Task ValidatorWithErrors_PreventsHandlerExecution()
    {
        var services = new ServiceCollection();
        services.AddSingleton<VoidCounter>();
        services.AddMediator(x => x.AddValidationMiddleware().AddHandler<VoidHandler>());
        services.AddSingleton<IValidator<VoidCommand>>(new FakeValidator<VoidCommand>(new ValidationError("Name", "required", "REQ")));

        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await Assert.ThrowsAsync<ValidationException>(() => mediator.Request(new VoidCommand()).AsTask());
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<VoidCounter>().Count);
    }

    [Fact]
    public async Task MultipleValidators_AggregateErrors()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddValidationMiddleware().AddHandler<EchoHandler>());
        services.AddSingleton<IValidator<EchoCommand>>(new FakeValidator<EchoCommand>(new ValidationError("A", "a", "A")));
        services.AddSingleton<IValidator<EchoCommand>>(new FakeValidator<EchoCommand>(new ValidationError("B", "b", "B")));

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            mediator.Request<EchoCommand, string>(new EchoCommand("x")).AsTask());

        Assert.Equal(2, exception.Errors.Count);
        Assert.Contains(exception.Errors, x => x.Code == "A");
        Assert.Contains(exception.Errors, x => x.Code == "B");
    }

    [Fact]
    public async Task PassingValidators_AllowHandlerToRun()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddValidationMiddleware().AddHandler<EchoHandler>());
        services.AddSingleton<IValidator<EchoCommand>>(new FakeValidator<EchoCommand>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Request<EchoCommand, string>(new EchoCommand("ok"));
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task MultipleValidators_AllPassingNonArrayList_RunsHandler()
    {
        // Constructed directly with a List (not an array): exercises the non-array materialization in ValidatorRunner,
        // and two passing validators drive the zero-total aggregate short-circuit.
        var validators = new List<IValidator<VoidCommand>>
        {
            new FakeValidator<VoidCommand>(),
            new FakeValidator<VoidCommand>()
        };

        var middleware = new ValidationMiddleware<VoidCommand>(validators);

        var ran = false;
        await middleware.Handle(
            new VoidCommand(),
            (_, _) =>
            {
                ran = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(ran);
    }

    [Fact]
    public async Task MultipleValidators_AllPassingViaDi_RunsHandler()
    {
        var services = new ServiceCollection();
        services.AddMediator(x => x.AddValidationMiddleware().AddHandler<EchoHandler>());
        services.AddSingleton<IValidator<EchoCommand>>(new FakeValidator<EchoCommand>());
        services.AddSingleton<IValidator<EchoCommand>>(new FakeValidator<EchoCommand>());

        var mediator = services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Request<EchoCommand, string>(new EchoCommand("ok"));
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task ValidationMiddleware_DoesNotApplyToEvents()
    {
        // Regression: ValidationMiddleware<TMessage : IMessage> used to wrap each event handler, running the validators
        // once per handler. It is now constrained to IRequest.
        var services = new ServiceCollection();
        services.AddSingleton<EventCounter>();
        services.AddMediator(x => x
            .AddValidationMiddleware()
            .AddHandler<EventNoticeHandlerA>()
            .AddHandler<EventNoticeHandlerB>());

        services.AddSingleton<IValidator<EventNotice>, CountingEventValidator>();
        using var scope = services.BuildServiceProvider().CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Publish(new EventNotice());

        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<EventCounter>().Count);
    }

    public sealed record VoidCommand : IRequest;

    public sealed record EchoCommand(string Name) : IRequest<string>;

    public sealed class EchoHandler : IRequestHandler<EchoCommand, string>
    {
        public ValueTask<string> Handle(EchoCommand message, CancellationToken cancellationToken) =>
            ValueTask.FromResult(message.Name);
    }

    public sealed class VoidHandler(VoidCounter counter) : IRequestHandler<VoidCommand>
    {
        public ValueTask Handle(VoidCommand message, CancellationToken cancellationToken)
        {
            counter.Count++;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class VoidCounter
    {
        public int Count;
    }

    public sealed record EventNotice : IEvent;

    public sealed class EventCounter
    {
        public int Count;
    }

    public sealed class CountingEventValidator(EventCounter counter) : IValidator<EventNotice>
    {
        public ValidationResult Validate(object model) => new();

        public ValidationResult Validate(EventNotice model, Action<ValidationContext<EventNotice>>? configure = null) => new();

        public ValidationResult Validate(ValidationContext<EventNotice> context) => new();

        public ValueTask<ValidationResult> ValidateAsync(
            ValidationContext<EventNotice> context,
            CancellationToken cancellationToken = default
        )
        {
            counter.Count++;
            return ValueTask.FromResult(new ValidationResult());
        }

        public ValueTask<ValidationResult> ValidateAsync(object model, CancellationToken cancellationToken = default)
        {
            counter.Count++;
            return ValueTask.FromResult(new ValidationResult());
        }

        public ValueTask<ValidationResult> ValidateAsync(EventNotice model, CancellationToken cancellationToken = default)
        {
            counter.Count++;
            return ValueTask.FromResult(new ValidationResult());
        }

        public ValueTask<ValidationResult> ValidateAsync(
            EventNotice model,
            Action<ValidationContext<EventNotice>>? configure,
            CancellationToken cancellationToken = default
        )
        {
            counter.Count++;
            return ValueTask.FromResult(new ValidationResult());
        }
    }

    public sealed class EventNoticeHandlerA : IEventHandler<EventNotice>
    {
        public ValueTask Handle(EventNotice message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    public sealed class EventNoticeHandlerB : IEventHandler<EventNotice>
    {
        public ValueTask Handle(EventNotice message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}