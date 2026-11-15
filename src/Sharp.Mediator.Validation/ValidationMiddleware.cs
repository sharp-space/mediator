using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation;

/// <summary>
/// Runs every registered <see cref="IValidator{TMessage}" /> and throws <see cref="ValidationException" /> if any Error-severity entries are produced.
/// Warning and Information entries alone are non-blocking; when an Error exists, every recorded entry is included. Constrained to
/// <see cref="IRequest" /> so events are not validated once per handler.
/// </summary>
public sealed class ValidationMiddleware<TMessage>(IEnumerable<IValidator<TMessage>> validators) : IMiddleware<TMessage> where TMessage : IRequest
{
    private readonly IValidator<TMessage>[] _validators = validators as IValidator<TMessage>[] ?? [.. validators];

    /// <inheritdoc />
    public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken)
    {
        if (_validators.Length == 0) return next(message, cancellationToken);

        var failures = ValidatorRunner.CollectFailuresAsync(_validators, message, cancellationToken);
        if (!failures.IsCompletedSuccessfully) return Await(failures, message, next, cancellationToken);

        var errors = failures.Result;
        return errors.Length == 0 ? next(message, cancellationToken) : ValueTask.FromException(new ValidationException(errors));

        static async ValueTask Await(
            ValueTask<ValidationError[]> pending,
            TMessage message,
            MessageHandle<TMessage> next,
            CancellationToken cancellationToken
        )
        {
            var errors = await pending.ConfigureAwait(false);
            if (errors.Length != 0) throw new ValidationException(errors);

            await next(message, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Runs every registered <see cref="IValidator{TMessage}" /> and throws <see cref="ValidationException" /> if any Error-severity entries are produced.
/// Warning and Information entries alone are non-blocking; when an Error exists, every recorded entry is included. Used for requests with a response.
/// </summary>
public sealed class ValidationMiddleware<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators) :
    IMiddleware<TMessage, TResponse> where TMessage : IRequest<TResponse>
{
    private readonly IValidator<TMessage>[] _validators = validators as IValidator<TMessage>[] ?? [.. validators];

    /// <inheritdoc />
    public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        if (_validators.Length == 0) return next(message, cancellationToken);

        var failures = ValidatorRunner.CollectFailuresAsync(_validators, message, cancellationToken);
        if (!failures.IsCompletedSuccessfully) return Await(failures, message, next, cancellationToken);

        var errors = failures.Result;
        return errors.Length == 0
            ? next(message, cancellationToken)
            : ValueTask.FromException<TResponse>(new ValidationException(errors));

        static async ValueTask<TResponse> Await(
            ValueTask<ValidationError[]> pending,
            TMessage message,
            MessageHandle<TMessage, TResponse> next,
            CancellationToken cancellationToken
        )
        {
            var errors = await pending.ConfigureAwait(false);
            if (errors.Length != 0) throw new ValidationException(errors);

            return await next(message, cancellationToken).ConfigureAwait(false);
        }
    }
}