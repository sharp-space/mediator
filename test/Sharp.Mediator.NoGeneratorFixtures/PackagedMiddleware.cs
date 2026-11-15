namespace Sharp.Mediator.NoGeneratorFixtures;

/// <summary>Records which messages flowed through the packaged middleware.</summary>
public sealed class PackagedTrace
{
    private readonly List<string> _entries = [];
    private readonly Lock _sync = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    public void Add(string entry)
    {
        lock (_sync)
        {
            _entries.Add(entry);
        }
    }
}

/// <summary>Middleware-package stand-in for Sharp.Mediator.Validation: a public open generic in an assembly built without the generator.</summary>
public sealed class PackagedMiddleware<TMessage, TResponse>(PackagedTrace trace) :
    IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
{
    public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add($"packaged:{typeof(TMessage).Name}");
        return next(message, cancellationToken);
    }
}

/// <summary>Registers <see cref="PackagedMiddleware{TMessage, TResponse}" />, as a package's builder extension would.</summary>
public static class PackagedMiddlewareRegistration
{
    extension(MediatorBuilder builder)
    {
        /// <summary>Adds the packaged trace middleware, which every handler assembly's generator closes over its messages.</summary>
        public MediatorBuilder AddPackagedMiddleware() => builder.AddMiddleware(typeof(PackagedMiddleware<,>));
    }
}