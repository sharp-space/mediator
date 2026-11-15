namespace Sharp.Mediator.Benchmarks.Fixtures;

// How the handlers of Complete and Completed complete. Canceled completes through the caller's token, which the
// scenario cancels before the call.
public enum Completion
{
    Synchronous,
    Asynchronous,
    Faulted,
    Canceled
}

public sealed record Complete(Completion Completion) : IRequest<int>;

public sealed record Completed(Completion Completion) : IEvent;

public sealed class CompleteHandler : IRequestHandler<Complete, int>
{
    public async ValueTask<int> Handle(Complete message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (message.Completion)
        {
            case Completion.Asynchronous:
                await Task.Yield();
                break;
            case Completion.Faulted:
                throw new InvalidOperationException();
            case Completion.Synchronous:
            case Completion.Canceled:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message), message.Completion, "Unsupported completion mode.");
        }

        return 42;
    }
}

public sealed class FirstCompletedHandler : IEventHandler<Completed>
{
    public async ValueTask Handle(Completed message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (message.Completion)
        {
            case Completion.Asynchronous:
                await Task.Yield();
                break;
            case Completion.Faulted:
                throw new InvalidOperationException();
            case Completion.Synchronous:
            case Completion.Canceled:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message), message.Completion, "Unsupported completion mode.");
        }
    }
}

public sealed class SecondCompletedHandler : IEventHandler<Completed>
{
    public async ValueTask Handle(Completed message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (message.Completion)
        {
            case Completion.Asynchronous:
                await Task.Yield();
                break;
            case Completion.Faulted:
                throw new InvalidOperationException();
            case Completion.Synchronous:
            case Completion.Canceled:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message), message.Completion, "Unsupported completion mode.");
        }
    }
}