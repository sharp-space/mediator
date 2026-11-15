using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Responses;
using Sharp.Mediator.Validation.Tests.Fixtures;
using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation.Tests;

public sealed class ValidatorRunnerContractTests
{
    // ---- each middleware, directly: a validator that suspends takes its slower path ---------------------------------

    private static readonly ValidationError Failure = new("Value", "invalid", "ASYNC");

    [Theory]
    [InlineData(ValidationErrorSeverity.Warning)]
    [InlineData(ValidationErrorSeverity.Information)]
    public async Task NonErrorSeverity_DoesNotBlockHandler(ValidationErrorSeverity severity)
    {
        var validator = new FakeValidator<Command>(new ValidationError("Value", "notice", "NOTICE", severity));
        await using var provider = BuildProvider(validator);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var response = await mediator.Request<Command, CommandResponse>(new Command());
        Assert.Equal(ResponseStatus.Success, response.Status);
        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<HandlerCounter>().Count);
        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Fact]
    public async Task ErrorSeverity_ReturnsInvalidWithoutRunningHandler()
    {
        var validator = new FakeValidator<Command>(new ValidationError("Value", "invalid", "ERROR"));
        await using var provider = BuildProvider(validator);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Request<Command, CommandResponse>(new Command());
        Assert.Equal(ResponseStatus.Invalid, response.Status);
        Assert.Equal("ERROR", Assert.Single(response.ValidationErrors).Code);
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<HandlerCounter>().Count);
    }

    [Fact]
    public async Task ErrorAndNonErrorEntries_ReturnInvalidWithEveryEntryInRegistrationOrder()
    {
        var warning = new FakeValidator<Command>(new ValidationError(
            "Warning", "notice", "WARNING", ValidationErrorSeverity.Warning));

        var error = new FakeValidator<Command>(new ValidationError("Error", "invalid", "ERROR"));

        await using var provider = BuildProvider(warning, error);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Request<Command, CommandResponse>(new Command());
        Assert.Equal(ResponseStatus.Invalid, response.Status);
        Assert.Equal(["WARNING", "ERROR"], response.ValidationErrors.Select(static x => x.Code!).ToArray());
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<HandlerCounter>().Count);
    }

    [Fact]
    public async Task MultipleValidators_ReportEntriesInRegistrationOrder()
    {
        var firstCompletion = new TaskCompletionSource<ValidationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new DelegatingValidator<Command>(_ => new ValueTask<ValidationResult>(firstCompletion.Task));
        var second = new DelegatingValidator<Command>(_ => ValueTask.FromResult(new ValidationResult([
            new ValidationError("Second", "invalid", "SECOND")
        ])));

        await using var provider = BuildProvider(first, second);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var request = mediator.Request<Command, CommandResponse>(new Command()).AsTask();
        Assert.False(request.IsCompleted);

        firstCompletion.SetResult(new([new("First", "invalid", "FIRST")]));
        var response = await request;
        Assert.Equal(["FIRST", "SECOND"], response.ValidationErrors.Select(static x => x.Code!).ToArray());
    }

    [Fact]
    public async Task MultipleValidators_RunOneAtATime()
    {
        // Validators share the message's DI scope, so overlapping them fails with services that reject concurrent use,
        // such as a DbContext.
        var firstCompletion = new TaskCompletionSource<ValidationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInvoked = false;
        var first = new DelegatingValidator<Command>(_ => new ValueTask<ValidationResult>(firstCompletion.Task));
        var second = new DelegatingValidator<Command>(_ =>
        {
            secondInvoked = true;
            return ValueTask.FromResult(new ValidationResult());
        });

        await using var provider = BuildProvider(first, second);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var request = mediator.Request<Command, CommandResponse>(new Command()).AsTask();

        Assert.False(secondInvoked, "a validator must not start while the previous one is running");

        firstCompletion.SetResult(new ValidationResult());
        await request;

        Assert.True(secondInvoked);
    }

    [Fact]
    public async Task SynchronousThrow_PropagatesWithoutRunningLaterValidators()
    {
        var laterInvoked = false;
        var throwing = new DelegatingValidator<Command>(_ => throw new InvalidOperationException("sync-validator-failure"));
        var later = new DelegatingValidator<Command>(_ =>
        {
            laterInvoked = true;
            return ValueTask.FromResult(new ValidationResult());
        });

        await using var provider = BuildProvider(throwing, later);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Request<Command, CommandResponse>(new Command()).AsTask());

        Assert.Equal("sync-validator-failure", exception.Message);
        Assert.False(laterInvoked);
    }

    [Fact]
    public async Task SingleAsynchronousValidator_IsAwaited()
    {
        var completion = new TaskCompletionSource<ValidationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var validator = new DelegatingValidator<Command>(_ => new ValueTask<ValidationResult>(completion.Task));
        await using var provider = BuildProvider(validator);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var request = mediator.Request<Command, CommandResponse>(new Command()).AsTask();
        Assert.False(request.IsCompleted);

        completion.SetResult(new([new("Value", "invalid", "ASYNC")]));
        var response = await request;
        Assert.Equal(ResponseStatus.Invalid, response.Status);
        Assert.Equal("ASYNC", Assert.Single(response.ValidationErrors).Code);
    }

    [Fact]
    public async Task SingleSynchronousThrow_IsReturnedAsFaultedValueTask()
    {
        var validator = new DelegatingValidator<Command>(_ =>
            throw new InvalidOperationException("single-sync-validator-failure"));

        await using var provider = BuildProvider(validator);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Request<Command, CommandResponse>(new Command()).AsTask());

        Assert.Equal("single-sync-validator-failure", exception.Message);
    }

    // The test resumes the validator only after the middleware has returned, so the middleware always meets a validator
    // that has not finished. These tests used to suspend it with Task.Yield, whose continuation could finish it on
    // another thread before the middleware looked: under load the middleware then took its faster path, the slower one
    // went unrun, and the coverage gate failed although every test passed.
    private static (DelegatingValidator<T> Validator, TaskCompletionSource<ValidationResult> Resume) Suspending<T>()
    {
        var resume = new TaskCompletionSource<ValidationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (new DelegatingValidator<T>(_ => new ValueTask<ValidationResult>(resume.Task)), resume);
    }

    [Fact]
    public async Task VoidMiddleware_SuspendingValidatorThatPasses_ReachesTheHandler()
    {
        var reached = false;
        var (validator, resume) = Suspending<VoidCommand>();
        var middleware = new ValidationMiddleware<VoidCommand>([validator]);

        var handled = middleware.Handle(new VoidCommand(), (_, _) =>
        {
            reached = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.False(handled.IsCompleted);

        resume.SetResult(new ValidationResult());
        await handled;

        Assert.True(reached);
    }

    [Fact]
    public async Task VoidMiddleware_SuspendingValidatorThatFails_Throws()
    {
        var (validator, resume) = Suspending<VoidCommand>();
        var middleware = new ValidationMiddleware<VoidCommand>([validator]);

        var handled = middleware.Handle(
            new VoidCommand(), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        Assert.False(handled.IsCompleted);

        resume.SetResult(new ValidationResult([Failure]));

        await Assert.ThrowsAsync<ValidationException>(handled.AsTask);
    }

    [Fact]
    public async Task ResponseMiddleware_SuspendingValidatorThatPasses_ReachesTheHandler()
    {
        var (validator, resume) = Suspending<ValueCommand>();
        var middleware = new ValidationMiddleware<ValueCommand, int>([validator]);

        var handled = middleware.Handle(
            new ValueCommand(), static (_, _) => new ValueTask<int>(1), CancellationToken.None);

        Assert.False(handled.IsCompleted);

        resume.SetResult(new ValidationResult());

        Assert.Equal(1, await handled);
    }

    [Fact]
    public async Task ResponseMiddleware_SuspendingValidatorThatFails_Throws()
    {
        var (validator, resume) = Suspending<ValueCommand>();
        var middleware = new ValidationMiddleware<ValueCommand, int>([validator]);

        var handled = middleware.Handle(
            new ValueCommand(), static (_, _) => new ValueTask<int>(1), CancellationToken.None);

        Assert.False(handled.IsCompleted);

        resume.SetResult(new ValidationResult([Failure]));

        await Assert.ThrowsAsync<ValidationException>(handled.AsTask);
    }

    // ---- validators reach a middleware constructed outside DI as any enumerable -------------------------------------

    [Fact]
    public async Task VoidMiddleware_AcceptsAnyEnumerable()
    {
        var validator = new FakeValidator<VoidCommand>();
        var middleware = new ValidationMiddleware<VoidCommand>(new List<IValidator<VoidCommand>> { validator });
        await middleware.Handle(new VoidCommand(), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Fact]
    public async Task ResponseMiddleware_AcceptsAnyEnumerable()
    {
        var validator = new FakeValidator<ValueCommand>();
        var middleware = new ValidationMiddleware<ValueCommand, int>(new List<IValidator<ValueCommand>> { validator });
        await middleware.Handle(new ValueCommand(), static (_, _) => new ValueTask<int>(1), CancellationToken.None);

        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Fact]
    public async Task ResponseValidationMiddleware_AcceptsAnyEnumerable()
    {
        var validator = new FakeValidator<Command>();
        var middleware = new ResponseValidationMiddleware<Command, CommandResponse>(new List<IValidator<Command>> { validator });
        await middleware.Handle(new Command(), static (_, _) => new ValueTask<CommandResponse>(new CommandResponse()), CancellationToken.None);

        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Fact]
    public async Task StreamMiddleware_AcceptsAnyEnumerable()
    {
        var validator = new FakeValidator<StreamCommand>();
        var middleware = new ValidationStreamMiddleware<StreamCommand, int>(new List<IValidator<StreamCommand>> { validator });
        await foreach (var _ in middleware.Handle(new StreamCommand(), static (_, _) => AsyncEnumerable.Empty<int>(), CancellationToken.None))
            Assert.Fail("the stream is empty");

        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Fact]
    public void StreamMiddleware_WithoutValidators_ReturnsTheNextStreamItself()
    {
        var stream = AsyncEnumerable.Empty<int>();
        var middleware = new ValidationStreamMiddleware<StreamCommand, int>([]);
        Assert.Same(stream, middleware.Handle(new StreamCommand(), (_, _) => stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // A consumer cancels a stream through the token of the call or the token it enumerates with, and a stream may
    // observe only the token its own enumerator receives, as EF Core's AsAsyncEnumerable does. A validated stream
    // used to pass the token on only as an argument of the next stage, so such a stream kept producing items after
    // the consumer canceled.
    public async Task StreamMiddleware_WithValidators_PassesCancellationToTheNextStreamsEnumerator(bool throughEnumeration)
    {
        var middleware = new ValidationStreamMiddleware<StreamCommand, int>([new FakeValidator<StreamCommand>()]);
        using var cancellation = new CancellationTokenSource();
        var stream = middleware.Handle(
            new StreamCommand(), static (_, _) => new EnumeratorTokenStream(),
            throughEnumeration ? CancellationToken.None : cancellation.Token);

        await using var enumerator = stream.GetAsyncEnumerator(throughEnumeration ? cancellation.Token : CancellationToken.None);
        Assert.True(await enumerator.MoveNextAsync());

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamMiddleware_WithDifferentCallAndEnumerationTokens_ObservesEitherCancellation(bool cancelCallToken)
    {
        var middleware = new ValidationStreamMiddleware<StreamCommand, int>([new FakeValidator<StreamCommand>()]);
        using var callCancellation = new CancellationTokenSource();
        using var enumerationCancellation = new CancellationTokenSource();
        var stream = middleware.Handle(
            new StreamCommand(), static (_, _) => new EnumeratorTokenStream(), callCancellation.Token);

        await using var enumerator = stream.GetAsyncEnumerator(enumerationCancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());

        await (cancelCallToken ? callCancellation : enumerationCancellation).CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    private static ServiceProvider BuildProvider(params IValidator<Command>[] validators)
    {
        var services = new ServiceCollection();
        services.AddSingleton<HandlerCounter>();
        foreach (var validator in validators)
            services.AddSingleton(validator);

        services.AddMediator(builder => builder
            .AddResponseValidationMiddleware()
            .AddHandler<CommandHandler>());

        return services.BuildServiceProvider();
    }

    public sealed record Command : IRequest<CommandResponse>;

    public sealed class CommandResponse : Response;

    public sealed class HandlerCounter
    {
        public int Count;
    }

    public sealed class CommandHandler(HandlerCounter counter) : IRequestHandler<Command, CommandResponse>
    {
        public ValueTask<CommandResponse> Handle(Command message, CancellationToken cancellationToken)
        {
            counter.Count++;
            return ValueTask.FromResult(new CommandResponse { Status = ResponseStatus.Success });
        }
    }

    public sealed record VoidCommand : IRequest;

    public sealed record ValueCommand : IRequest<int>;

    public sealed record StreamCommand : IStreamRequest<int>;

    // Endless, and canceled only through the token GetAsyncEnumerator receives.
    private sealed class EnumeratorTokenStream : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(cancellationToken);

        private sealed class Enumerator(CancellationToken cancellationToken) : IAsyncEnumerator<int>
        {
            public int Current { get; private set; }

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                Current++;

                return ValueTask.FromResult(true);
            }

            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class DelegatingValidator<T>(Func<CancellationToken, ValueTask<ValidationResult>> validate) :
        IValidator<T>
    {
        public ValidationResult Validate(object model) => throw new NotSupportedException();

        public ValidationResult Validate(T model, Action<ValidationContext<T>>? configure = null) =>
            throw new NotSupportedException();

        public ValidationResult Validate(ValidationContext<T> context) => throw new NotSupportedException();

        public ValueTask<ValidationResult> ValidateAsync(
            ValidationContext<T> context,
            CancellationToken cancellationToken = default
        ) => validate(cancellationToken);

        public ValueTask<ValidationResult> ValidateAsync(
            object model,
            CancellationToken cancellationToken = default
        ) => validate(cancellationToken);

        public ValueTask<ValidationResult> ValidateAsync(
            T model,
            CancellationToken cancellationToken = default
        ) => validate(cancellationToken);

        public ValueTask<ValidationResult> ValidateAsync(
            T model,
            Action<ValidationContext<T>>? configure,
            CancellationToken cancellationToken = default
        ) => validate(cancellationToken);
    }
}