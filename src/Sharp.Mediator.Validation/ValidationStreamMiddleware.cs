using System.Runtime.CompilerServices;
using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation;

/// <summary>
/// Runs every registered <see cref="IValidator{TMessage}" /> before the stream starts and throws <see cref="ValidationException" /> if any
/// Error-severity entries are produced. Warning and Information entries alone are non-blocking. A stream has no response envelope to return failures in,
/// so this always throws, on the first <c>MoveNextAsync</c> rather than at the <c>RequestStream</c> call.
/// </summary>
public sealed class ValidationStreamMiddleware<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators) :
    IStreamMiddleware<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    private readonly IValidator<TRequest>[] _validators = validators as IValidator<TRequest>[] ?? [.. validators];

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> Handle(
        TRequest message,
        StreamMessageHandle<TRequest, TResponse> next,
        CancellationToken cancellationToken
    ) => _validators.Length == 0 ? next(message, cancellationToken) : Validated(message, next, cancellationToken);

    private async IAsyncEnumerable<TResponse> Validated(
        TRequest message,
        StreamMessageHandle<TRequest, TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var errors = await ValidatorRunner.CollectFailuresAsync(_validators, message, cancellationToken).ConfigureAwait(false);
        if (errors.Length != 0) throw new ValidationException(errors);

        // ReSharper disable once RedundantWithCancellation — the mediator passes the token on to the enumerator of the
        // handler's stream only, and the next stream may be another middleware's, which may observe only the token it
        // is enumerated with.
        var items = next(message, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false);
        await foreach (var item in items) yield return item;
    }
}