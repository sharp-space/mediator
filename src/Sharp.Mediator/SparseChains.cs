namespace Sharp.Mediator;

/// <summary>
/// The chains a mediator caches for slots past the end of its dense array, while it caches few of them: an open-addressing table keyed by slot, which
/// holds a few chains in a few entries wherever their slots lie.
/// </summary>
/// <remarks>
/// The mediator changes the table only under the lock of its dense array, which holds the table, and dispatch reads it without one. An entry's slot
/// never changes once it is set, and a growth builds a new array of entries that replaces the old one whole, so a read meets an entry of its own slot,
/// an empty entry, or one of another slot.
/// </remarks>
internal sealed class SparseChains
{
    private int _count;

    // A power of two in length, at most three quarters full, so a probe always reaches an empty entry.
    private Entry[] _entries = new Entry[2];

    // Dispatches that found their chain here. Counted without a lock, so concurrent dispatches can lose a count, which
    // only delays the move it leads to.
    private int _hits;

    /// <summary>A table that holds <paramref name="chain" /> for <paramref name="slot" />.</summary>
    public SparseChains(int slot, object chain)
    {
        Add(_entries, slot, chain);
        _count = 1;
        HighestSlot = slot;
    }

    /// <summary>The highest slot the table holds a chain for.</summary>
    public int HighestSlot { get; private set; }

    /// <summary>The chain cached for <paramref name="slot" />, or null.</summary>
    public object? Find(int slot)
    {
        var entries = Volatile.Read(ref _entries);
        var mask = entries.Length - 1;
        for (var i = slot & mask;; i = (i + 1) & mask)
        {
            // The chain is written before the slot, so an entry that shows its slot shows its chain.
            var found = Volatile.Read(ref entries[i].Slot);
            if (found == slot) return entries[i].Chain;
            if (found == 0) return null;
        }
    }

    /// <summary>
    /// Caches <paramref name="chain" /> for <paramref name="slot" />, or returns false when the table is full and a dense array of every slot it would hold
    /// takes no more memory than a larger table, so the mediator moves its chains there.
    /// </summary>
    public bool TryAdd(int slot, object chain)
    {
        var entries = _entries;
        if (IndexOf(entries, slot) is var index and >= 0)
        {
            Volatile.Write(ref entries[index].Chain, chain);
            return true;
        }

        if ((_count + 1) * 4 > entries.Length * 3)
        {
            // An entry takes twice the memory of a slot of the dense array.
            var length = entries.Length * 2;
            if (Math.Max(HighestSlot, slot) < 2 * length) return false;

            var grown = new Entry[length];
            foreach (var entry in entries)
                if (entry.Slot != 0)
                    Add(grown, entry.Slot, entry.Chain!);

            Add(grown, slot, chain);
            Volatile.Write(ref _entries, grown);
        }
        else
            Add(entries, slot, chain);

        _count++;
        HighestSlot = Math.Max(HighestSlot, slot);

        return true;
    }

    /// <summary>
    /// Counts a dispatch that found its chain here, and returns true for the one that brings the count to a quarter of the highest slot: from then on the
    /// mediator keeps its chains in an array of every slot, where a dispatch finds its chain without coming here.
    /// </summary>
    public bool CountHit() => ++_hits == HighestSlot / 4;

    /// <summary>Writes every chain into its slot of <paramref name="dense" />, which holds the table's slots.</summary>
    public void CopyTo(object?[] dense)
    {
        foreach (var entry in _entries)
            if (entry.Slot != 0)
                dense[entry.Slot] = entry.Chain;
    }

    private static int IndexOf(Entry[] entries, int slot)
    {
        var mask = entries.Length - 1;
        for (var i = slot & mask;; i = (i + 1) & mask)
        {
            if (entries[i].Slot == slot) return i;
            if (entries[i].Slot == 0) return -1;
        }
    }

    // Into an entry no read can have matched yet, so the chain is in place before the slot shows the entry as taken.
    private static void Add(Entry[] entries, int slot, object chain)
    {
        var mask = entries.Length - 1;
        var i = slot & mask;
        while (entries[i].Slot != 0) i = (i + 1) & mask;

        entries[i].Chain = chain;
        Volatile.Write(ref entries[i].Slot, slot);
    }

    // No message takes slot 0, so a slot of 0 marks an empty entry.
    private struct Entry
    {
        public object? Chain;

        public int Slot;
    }
}