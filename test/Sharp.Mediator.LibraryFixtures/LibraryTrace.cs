namespace Sharp.Mediator.LibraryFixtures;

/// <summary>Records handler executions across assemblies.</summary>
public sealed class LibraryTrace
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