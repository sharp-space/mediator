using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation.Tests.Fixtures;

public sealed class FakeValidator<TModel>(params ValidationError[] errors) : IValidator<TModel>
{
    private int _asyncInvocationCount;

    public int AsyncInvocationCount => Volatile.Read(ref _asyncInvocationCount);

    public ValidationResult Validate(object model) => Build();

    public ValidationResult Validate(ValidationContext<TModel> context) => Build();

    public ValueTask<ValidationResult> ValidateAsync(
        ValidationContext<TModel> context,
        CancellationToken cancellationToken = default
    ) => BuildAsync();

    public ValidationResult Validate(TModel model, Action<ValidationContext<TModel>>? configure = null) => Build();

    public ValueTask<ValidationResult> ValidateAsync(object model, CancellationToken cancellationToken = default) =>
        BuildAsync();

    public ValueTask<ValidationResult> ValidateAsync(TModel model, CancellationToken cancellationToken = default) =>
        BuildAsync();

    public ValueTask<ValidationResult> ValidateAsync(
        TModel model,
        Action<ValidationContext<TModel>>? configure,
        CancellationToken cancellationToken = default
    ) => BuildAsync();

    private ValueTask<ValidationResult> BuildAsync()
    {
        Interlocked.Increment(ref _asyncInvocationCount);
        return ValueTask.FromResult(Build());
    }

    private ValidationResult Build() => new([.. errors]);
}