using BenchmarkDotNet.Attributes;
using Sharp.Mediator.Validation;
using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>Validation middleware fast paths with no, one, and multiple passing validators.</summary>
[MemoryDiagnoser]
public class ValidationBenchmarks
{
    private static readonly Command Message = new();
    private static readonly PassingValidator Validator = new();
    private readonly ValidationMiddleware<Command, int> _multiple = new([Validator, Validator]);

    private readonly ValidationMiddleware<Command, int> _none = new([]);
    private readonly ValidationMiddleware<Command, int> _one = new([Validator]);

    [Benchmark(Baseline = true)]
    public ValueTask<int> NoValidator() => _none.Handle(Message, Next, CancellationToken.None);

    [Benchmark]
    public ValueTask<int> OnePassingValidator() => _one.Handle(Message, Next, CancellationToken.None);

    [Benchmark]
    public ValueTask<int> MultiplePassingValidators() => _multiple.Handle(Message, Next, CancellationToken.None);

    private static ValueTask<int> Next(Command message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(42);

    public sealed record Command : IRequest<int>;

    private sealed class PassingValidator : IValidator<Command>
    {
        private static readonly ValidationResult Valid = new();

        public ValidationResult Validate(object model) => Valid;

        public ValidationResult Validate(ValidationContext<Command> context) => Valid;

        public ValueTask<ValidationResult> ValidateAsync(
            ValidationContext<Command> context,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Valid);

        public ValidationResult Validate(
            Command model,
            Action<ValidationContext<Command>>? configure = null
        ) => Valid;

        public ValueTask<ValidationResult> ValidateAsync(
            object model,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Valid);

        public ValueTask<ValidationResult> ValidateAsync(
            Command model,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Valid);

        public ValueTask<ValidationResult> ValidateAsync(
            Command model,
            Action<ValidationContext<Command>>? configure,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Valid);
    }
}