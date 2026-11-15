using System.Runtime.CompilerServices;

namespace Sharp.Mediator.Bindings;

/// <summary>
/// Boxing adapters for the runtime-typed (<c>object</c>) dispatch paths: they wrap a handler's strongly-typed <see cref="ValueTask{TResult}" /> /
/// <see cref="IAsyncEnumerable{T}" /> into the object-returning contract. Called by the routes, which go through the mediator's typed (cached) methods
/// and then box the result here.
/// </summary>
/// <remarks>
/// The synchronous path returns a by-value <see cref="ValueTask{TResult}" /> (no outer task); only a value-type response is boxed.
/// </remarks>
internal static class MediatorBox
{
    /// <summary>Boxes a typed response value-task into <c>ValueTask&lt;object?&gt;</c>.</summary>
    public static ValueTask<object?> AsObject<TResponse>(ValueTask<TResponse> task) =>
        task.IsCompletedSuccessfully ? new ValueTask<object?>(task.Result) : Await(task);

    private static async ValueTask<object?> Await<TResponse>(ValueTask<TResponse> task) =>
        await task.ConfigureAwait(false);

    /// <summary>Adapts a void request's value-task to <c>ValueTask&lt;object?&gt;</c> with a null result.</summary>
    public static ValueTask<object?> AsObjectVoid(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully) return AwaitVoid(task);

        task.GetAwaiter().GetResult();

        return default;
    }

    private static async ValueTask<object?> AwaitVoid(ValueTask task)
    {
        await task.ConfigureAwait(false);
        return null;
    }

    /// <summary>Adapts a typed async stream to <c>IAsyncEnumerable&lt;object?&gt;</c>, boxing each item and preserving cancellation.</summary>
    public static async IAsyncEnumerable<object?> AsObjectStream<TResponse>(
        IAsyncEnumerable<TResponse> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return item;
    }
}