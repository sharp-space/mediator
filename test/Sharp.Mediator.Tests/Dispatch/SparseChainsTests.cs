namespace Sharp.Mediator.Tests.Dispatch;

// The table a mediator keeps the chains of high slots in while it caches few of them. Slots here are numbers that no
// message holds: the table only stores them. A table of length n places a slot at its value modulo n, and a slot that
// finds its place taken goes to the next free entry, wrapping at the end.
public sealed class SparseChainsTests
{
    [Fact]
    public void Find_SlotItHolds_ReturnsItsChain()
    {
        var chain = new object();
        var sparse = new SparseChains(40, chain);
        Assert.Same(chain, sparse.Find(40));
    }

    [Fact]
    public void Find_SlotItDoesNotHold_ReturnsNull()
    {
        var sparse = new SparseChains(40, new object());
        Assert.Null(sparse.Find(41));
    }

    [Fact]
    // In a table of four entries, 40 and 44 both belong at 0, and 43 and 47 at 3, the last entry: the second slot of
    // each pair is found past the first, and 47 at 0, past the end.
    public void Find_ReachesASlotPastAnotherThatTookItsPlace()
    {
        var first = new Chains(40);
        first.Add(44);
        Assert.Same(first[40], first.Sparse.Find(40));
        Assert.Same(first[44], first.Sparse.Find(44));
        Assert.Null(first.Sparse.Find(48));

        var wrapped = new Chains(43);
        wrapped.Add(47);
        Assert.Same(wrapped[47], wrapped.Sparse.Find(47));
        Assert.Null(wrapped.Sparse.Find(51));
    }

    [Fact]
    public void TryAdd_ReplacesTheChainOfASlotItHolds()
    {
        var sparse = new SparseChains(40, new object());
        var replacement = new object();
        Assert.True(sparse.TryAdd(40, replacement));
        Assert.Same(replacement, sparse.Find(40));
    }

    [Fact]
    // A table grows before it is three quarters full, so a search always ends at an empty entry, and keeps every chain.
    public void TryAdd_ManySlots_GrowsTableKeepingEveryChain()
    {
        var chains = new Chains(1000);
        for (var slot = 1001; slot < 1040; slot++) chains.Add(slot);

        for (var slot = 1000; slot < 1040; slot++) Assert.Same(chains[slot], chains.Sparse.Find(slot));

        Assert.Equal(1039, chains.Sparse.HighestSlot);
    }

    [Fact]
    // An entry takes as much memory as two slots of an array. When the table has to grow, an array of every slot it
    // would hold may take no more memory than the larger table, and the mediator then moves its chains into such an
    // array. Slots from 32 fill a table of 16 entries at 43; the table of 32 that 44 needs takes as much memory as an
    // array of 64 slots, which holds 44.
    public void TryAdd_RefusesToGrow_WhenAnArrayOfItsSlotsTakesNoMoreMemory()
    {
        var chains = new Chains(32);
        for (var slot = 33; slot <= 43; slot++) chains.Add(slot);

        Assert.False(chains.Sparse.TryAdd(44, new object()));
        Assert.Null(chains.Sparse.Find(44));

        for (var slot = 32; slot <= 43; slot++) Assert.Same(chains[slot], chains.Sparse.Find(slot));
    }

    [Fact]
    // A mediator moves the chains of a table it keeps finding them in into an array of every slot once those
    // finds number a quarter of the highest slot: 10 for a table whose highest slot is 40, and 250 once it holds
    // 1000 as well.
    public void CountHit_ReturnsTrueOnce_WhenTheHitsReachAQuarterOfTheHighestSlot()
    {
        var chains = new Chains(40);
        for (var hit = 1; hit < 10; hit++) Assert.False(chains.Sparse.CountHit());

        Assert.True(chains.Sparse.CountHit());
        Assert.False(chains.Sparse.CountHit());

        chains.Add(1000);
        for (var hit = 12; hit < 250; hit++) Assert.False(chains.Sparse.CountHit());

        Assert.True(chains.Sparse.CountHit());
    }

    [Fact]
    public void CopyTo_WritesEveryChainIntoItsSlot()
    {
        var chains = new Chains(40);
        chains.Add(44);
        chains.Add(1000);
        var array = new object?[1001];

        chains.Sparse.CopyTo(array);

        Assert.Equal(3, array.Count(static x => x is not null));
        Assert.Same(chains[40], array[40]);
        Assert.Same(chains[44], array[44]);
        Assert.Same(chains[1000], array[1000]);
    }

    // A table and the chain added for each slot, to compare with what the table finds.
    private sealed class Chains
    {
        private readonly Dictionary<int, object> _added = [];

        public Chains(int slot)
        {
            var chain = new object();
            Sparse = new SparseChains(slot, chain);
            _added[slot] = chain;
        }

        public SparseChains Sparse { get; }

        public object this[int slot] => _added[slot];

        public void Add(int slot)
        {
            var chain = new object();
            Assert.True(Sparse.TryAdd(slot, chain));

            _added[slot] = chain;
        }
    }
}