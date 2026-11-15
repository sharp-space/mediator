using Sharp.Mediator.Responses;
using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation;

/// <summary>
/// Runs every registered <see cref="IValidator{TMessage}" /> and, if any Error-severity entries are produced, returns a new
/// <typeparamref name="TResponse" /> with <see cref="ResponseStatus.Invalid" /> instead of throwing. Warning and Information entries alone are
/// non-blocking; when an Error exists, every recorded entry is included. For handlers that return an <see cref="IResponse" />, so that failures come back
/// as data rather than as an exception.
/// </summary>
public sealed class ResponseValidationMiddleware<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators) :
    IMiddleware<TMessage, TResponse> where TMessage : IRequest<TResponse> where TResponse : IResponse, new()
{
    private readonly IValidator<TMessage>[] _validators = validators as IValidator<TMessage>[] ?? [.. validators];

    /// <inheritdoc />
    public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        if (_validators.Length == 0) return next(message, cancellationToken);

        var failures = ValidatorRunner.CollectFailuresAsync(_validators, message, cancellationToken);
        if (!failures.IsCompletedSuccessfully) return Await(failures, message, next, cancellationToken);

        var errors = failures.Result;
        return errors.Length == 0 ? next(message, cancellationToken) : new ValueTask<TResponse>(Invalid(errors));

        static async ValueTask<TResponse> Await(
            ValueTask<ValidationError[]> pending,
            TMessage message,
            MessageHandle<TMessage, TResponse> next,
            CancellationToken cancellationToken
        )
        {
            var errors = await pending.ConfigureAwait(false);
            return errors.Length == 0 ? await next(message, cancellationToken).ConfigureAwait(false) : Invalid(errors);
        }
    }

    private static TResponse Invalid(ValidationError[] errors) =>
        new() { Status = ResponseStatus.Invalid, ValidationErrors = errors };
}