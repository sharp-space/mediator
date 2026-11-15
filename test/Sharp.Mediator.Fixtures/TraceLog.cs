namespace Sharp.Mediator.Fixtures;

// Records the order handlers and middleware ran in (injected through DI).
public sealed class TraceLog
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