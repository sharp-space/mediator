using Sharp.Validator;
using Sharp.Validator.Result;

namespace Sharp.Mediator.Validation;

internal static class ValidatorRunner
{
    /// <summary>Runs <paramref name="validators" />, which callers have checked is not empty.</summary>
    public static ValueTask<ValidationError[]> CollectFailuresAsync<T>(IValidator<T>[] validators, T message, CancellationToken cancellationToken) =>
        validators.Length == 1
            ? CollectSingleAsync(validators[0], message, cancellationToken)
            : CollectMultiple(validators, message, cancellationToken);

    private static ValueTask<ValidationError[]> CollectSingleAsync<T>(IValidator<T> validator, T message, CancellationToken cancellationToken)
    {
        try
        {
            var task = validator.ValidateAsync(message, cancellationToken);
            return task.IsCompletedSuccessfully
                ? ValueTask.FromResult(GetFailures(task.Result))
                : Await(task);
        }
        catch (Exception exception)
        {
            return ValueTask.FromException<ValidationError[]>(exception);
        }

        static async ValueTask<ValidationError[]> Await(ValueTask<ValidationResult> pending) =>
            GetFailures(await pending.ConfigureAwait(false));
    }

    // One at a time: validators of a message share its DI scope, and scoped dependencies such as a DbContext do not
    // allow concurrent use. Every validator still runs, so the caller sees all failures at once. Validators without
    // entries touch no list, and those that finish synchronously no state machine either.
    private static ValueTask<ValidationError[]> CollectMultiple<T>(IValidator<T>[] validators, T message, CancellationToken cancellationToken)
    {
        List<ValidationError>? entries = null;
        var hasBlockingError = false;
        for (var i = 0; i < validators.Length; i++)
        {
            ValueTask<ValidationResult> task;

            try
            {
                task = validators[i].ValidateAsync(message, cancellationToken);
            }
            catch (Exception exception)
            {
                return ValueTask.FromException<ValidationError[]>(exception);
            }

            if (!task.IsCompletedSuccessfully)
                return CollectRemainingAsync(validators, i, task, entries, hasBlockingError, message, cancellationToken);

            var result = task.Result.Errors;
            if (result.Count == 0) continue;

            (entries ??= []).AddRange(result);
            hasBlockingError |= HasBlockingError(result);
        }

        return ValueTask.FromResult<ValidationError[]>(hasBlockingError ? [.. entries!] : []);
    }

    private static async ValueTask<ValidationError[]> CollectRemainingAsync<T>(
        IValidator<T>[] validators,
        int index,
        ValueTask<ValidationResult> pending,
        List<ValidationError>? entries,
        bool hasBlockingError,
        T message,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var result = (await pending.ConfigureAwait(false)).Errors;
            if (result.Count != 0)
            {
                (entries ??= []).AddRange(result);
                hasBlockingError |= HasBlockingError(result);
            }

            if (++index == validators.Length) break;

            pending = validators[index].ValidateAsync(message, cancellationToken);
        }

        return hasBlockingError ? [.. entries!] : [];
    }

    private static bool HasBlockingError(ValidationErrorCollection entries)
    {
        foreach (var entry in entries)
            if (entry.Severity == ValidationErrorSeverity.Error)
                return true;

        return false;
    }

    private static ValidationError[] GetFailures(ValidationResult result)
    {
        var entries = result.Errors;
        return HasBlockingError(entries) ? [.. entries] : [];
    }
}