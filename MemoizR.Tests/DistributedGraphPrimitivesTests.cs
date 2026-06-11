namespace MemoizR.Tests;

// Step 1 toward the distributed reaction graph (issue #39): stamp dominance (the lattice order
// a sync layer uses to order observations and drop late deliveries) and per-context node-id
// slices (so peers' stamps can never collide on an id).
public class DistributedGraphPrimitivesTests
{
    private const long TestEpoch = 5;

    private static CausalityStamp Stamp(params (int Id, long Trigger)[] entries) =>
        CausalityStamp.JoinAll(entries.Select(e => CausalityStamp.ForSignal(e.Id, e.Trigger, TestEpoch)));

    [Fact]
    public void IsDominatedBy_IsTheLatticeOrderOfJoin()
    {
        var a = Stamp((1, 4), (2, 1));
        var b = Stamp((2, 7), (3, 2));
        var joined = a.Join(b);

        // Reflexive, and the join dominates both operands.
        Assert.True(a.IsDominatedBy(a));
        Assert.True(a.IsDominatedBy(joined));
        Assert.True(b.IsDominatedBy(joined));
        Assert.False(joined.IsDominatedBy(a));
        Assert.False(joined.IsDominatedBy(b));

        // Strictly newer trigger dominates; a subset with equal triggers is dominated.
        Assert.True(Stamp((1, 4)).IsDominatedBy(Stamp((1, 5))));
        Assert.False(Stamp((1, 5)).IsDominatedBy(Stamp((1, 4))));
        Assert.True(Stamp((1, 4)).IsDominatedBy(Stamp((1, 4), (2, 7))));
        Assert.False(Stamp((1, 4), (2, 7)).IsDominatedBy(Stamp((1, 4))));

        // Concurrent observations: each reflects a write the other has not seen.
        var left = Stamp((1, 5), (2, 1));
        var right = Stamp((1, 4), (2, 2));
        Assert.False(left.IsDominatedBy(right));
        Assert.False(right.IsDominatedBy(left));

        // The empty stamp claims nothing.
        Assert.True(CausalityStamp.Empty.IsDominatedBy(a));
        Assert.True(CausalityStamp.Empty.IsDominatedBy(CausalityStamp.Empty));
        Assert.False(a.IsDominatedBy(CausalityStamp.Empty));
    }

    [Fact]
    public void IsDominatedBy_AcrossEpochs_IsIncomparable()
    {
        var a = CausalityStamp.ForSignal(1, 4, 5);
        var b = CausalityStamp.ForSignal(1, 9, 6);

        Assert.False(a.IsDominatedBy(b));
        Assert.False(b.IsDominatedBy(a));
    }

    [Fact]
    public async Task LateDelivery_IsDroppableWhenDominated()
    {
        // The sync-layer pattern: a VALUE message races a newer one; the stamp decides. A
        // payload whose stamp is dominated by what the consumer already holds is old news --
        // including an exact duplicate, which is dominated both ways.
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        await s.Set(2);
        var inFlight = s.Stamp.Serialize(); // trigger 1 leaves the host

        await s.Set(3); // trigger 2 overtakes it
        var current = s.Stamp;

        var late = CausalityStamp.Deserialize(inFlight);
        Assert.True(late.IsDominatedBy(current));   // drop
        Assert.False(current.IsDominatedBy(late));  // and never the other way around

        var duplicate = CausalityStamp.Deserialize(current.Serialize());
        Assert.True(duplicate.IsDominatedBy(current));
        Assert.True(current.IsDominatedBy(duplicate));
    }

    [Fact]
    public void IdSlices_KeepPeersDisjoint_AndMergedStampsCompact()
    {
        // Two "peers" carve the id space into contiguous slices.
        var peerA = new MemoFactory(null, 1024, 2048);
        var peerB = new MemoFactory(null, 2048, 3072);

        var a1 = peerA.CreateSignal(1);
        var a2 = peerA.CreateSignal(2);
        var b1 = peerB.CreateSignal(3);

        Assert.Equal(1024, a1.Id);
        Assert.Equal(1025, a2.Id);
        Assert.Equal(2048, b1.Id);

        // What a consumer-side stamp spanning both peers will look like once the bridge
        // normalizes epochs: disjoint contiguous slices merge collision-free, each peer in its
        // own subtree of the interval encoding.
        var merged = Stamp((a1.Id, 4), (a2.Id, 4), (b1.Id, 7));
        Assert.True(merged.TryGetTrigger(a1.Id, out var ta));
        Assert.Equal(4, ta);
        Assert.True(merged.TryGetTrigger(b1.Id, out var tb));
        Assert.Equal(7, tb);
        Assert.Equal(3, merged.Triggers.Count);
    }

    [Fact]
    public void IdSlice_ExhaustionThrows_InsteadOfBleedingIntoANeighbour()
    {
        var f = new MemoFactory(null, 10, 12);
        Assert.Equal(10, f.CreateSignal(1).Id);
        Assert.Equal(11, f.CreateSignal(2).Id);

        Assert.Throws<InvalidOperationException>(() => f.CreateSignal(3));
    }

    [Fact]
    public void IdSlice_InvalidBoundsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoFactory(null, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoFactory(null, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoFactory(null, 10, 5));
    }

    [Fact]
    public void KeyedContext_CannotBeReboundToADifferentSlice()
    {
        var key = $"slice-{Guid.NewGuid()}";
        var first = new MemoFactory(key, 100, 200);
        var sameSlice = new MemoFactory(key, 100, 200); // sharing the context is fine

        Assert.Same(first.Context, sameSlice.Context);
        Assert.Throws<ArgumentException>(() => new MemoFactory(key, 100, 300));
        Assert.Throws<ArgumentException>(() => new MemoFactory(key)); // default slice differs too

        GC.KeepAlive(first);
    }

    [Fact]
    public void DefaultFactory_KeepsTheHistoricIdSequence()
    {
        var f = new MemoFactory();
        Assert.Equal(1, f.CreateSignal(0).Id);
        Assert.Equal(2, f.CreateSignal(0).Id);
    }
}
