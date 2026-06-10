using MemoizR.Reactive;

namespace MemoizR.Tests;

// Phase 1 of the Causality Trigger Clock (issue #39): per-signal trigger counters, per-node
// causality stamps captured at source-read time, and the untorn (value, stamp) publication.
public class CausalityTriggerClockTests
{
    // Builds the expected stamp in the issue's notation, e.g. Stamp((s2, 7), (s3, 2)) == {#2: 7, #3: 2}.
    private static CausalityStamp Stamp(params (SignalHandlR Node, long Trigger)[] entries)
    {
        var stamp = CausalityStamp.Empty;
        foreach (var (node, trigger) in entries)
        {
            stamp = stamp.Join(CausalityStamp.ForSignal(node.Id, trigger));
        }
        return stamp;
    }

    private static long TriggerOf(SignalHandlR node)
    {
        Assert.True(node.Stamp.TryGetTrigger(node.Id, out var trigger));
        return trigger;
    }

    [Fact]
    public void Stamp_Empty_HasNoTriggers()
    {
        Assert.Empty(CausalityStamp.Empty.Triggers);
        Assert.False(CausalityStamp.Empty.TryGetTrigger(1, out _));
    }

    [Fact]
    public void Stamp_ForSignal_TracksSingleEntry()
    {
        var stamp = CausalityStamp.ForSignal(3, 7);
        Assert.True(stamp.TryGetTrigger(3, out var trigger));
        Assert.Equal(7, trigger);
        Assert.Single(stamp.Triggers);
    }

    [Fact]
    public void Stamp_Join_TakesPointwiseMaxOverUnion()
    {
        var a = CausalityStamp.ForSignal(1, 4).Join(CausalityStamp.ForSignal(2, 1));
        var b = CausalityStamp.ForSignal(2, 7).Join(CausalityStamp.ForSignal(3, 2));

        var joined = a.Join(b);

        Assert.Equal(CausalityStamp.ForSignal(1, 4).Join(CausalityStamp.ForSignal(2, 7)).Join(CausalityStamp.ForSignal(3, 2)), joined);
    }

    [Fact]
    public void Stamp_Join_IsCommutativeAssociativeIdempotent()
    {
        var a = CausalityStamp.ForSignal(1, 4).Join(CausalityStamp.ForSignal(2, 1));
        var b = CausalityStamp.ForSignal(2, 7);
        var c = CausalityStamp.ForSignal(3, 2);

        Assert.Equal(a.Join(b), b.Join(a));
        Assert.Equal(a.Join(b).Join(c), a.Join(b.Join(c)));
        Assert.Equal(a, a.Join(a));
        Assert.Equal(a, a.Join(CausalityStamp.Empty));
        Assert.Equal(a, CausalityStamp.Empty.Join(a));
    }

    [Fact]
    public void Stamp_IsConsistentWith_ChecksAgreementOnSharedSignals()
    {
        // The issue's example: memo #5 {1:4, 2:7, 3:2} and memo #6 {2:7, 3:2} agree on the
        // shared signals -> a glitch-free pair of inputs.
        var m5 = CausalityStamp.ForSignal(1, 4).Join(CausalityStamp.ForSignal(2, 7)).Join(CausalityStamp.ForSignal(3, 2));
        var m6 = CausalityStamp.ForSignal(2, 7).Join(CausalityStamp.ForSignal(3, 2));
        Assert.True(m5.IsConsistentWith(m6));
        Assert.True(m6.IsConsistentWith(m5));

        // One input reflecting a newer write of signal 2 than the other -> glitched.
        var m6Newer = CausalityStamp.ForSignal(2, 8).Join(CausalityStamp.ForSignal(3, 2));
        Assert.False(m5.IsConsistentWith(m6Newer));
        Assert.False(m6Newer.IsConsistentWith(m5));

        // No shared causality -> trivially consistent.
        Assert.True(CausalityStamp.ForSignal(1, 4).IsConsistentWith(CausalityStamp.ForSignal(2, 9)));
        Assert.True(CausalityStamp.Empty.IsConsistentWith(m5));
    }

    [Fact]
    public void Stamp_ValueEquality_IsOrderIndependent()
    {
        var a = CausalityStamp.ForSignal(1, 4).Join(CausalityStamp.ForSignal(2, 7));
        var b = CausalityStamp.ForSignal(2, 7).Join(CausalityStamp.ForSignal(1, 4));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, CausalityStamp.ForSignal(1, 4));
        Assert.NotEqual(a, a.Join(CausalityStamp.ForSignal(2, 8)));
    }

    [Fact]
    public void Nodes_GetDistinctIdsWithinContext()
    {
        var f = new MemoFactory();
        var s1 = f.CreateSignal(1);
        var s2 = f.CreateSignal(2);
        var m = f.CreateMemoizR(() => Task.FromResult(0));

        Assert.NotEqual(s1.Id, s2.Id);
        Assert.NotEqual(s1.Id, m.Id);
        Assert.NotEqual(s2.Id, m.Id);
    }

    [Fact]
    public void Signal_StartsAtTriggerZero()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(42);

        Assert.Equal(Stamp((s, 0)), s.Stamp);
        Assert.Empty(s.SourceStamps);
    }

    [Fact]
    public async Task Signal_SetToNewValue_BumpsTrigger()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);

        await s.Set(2);
        Assert.Equal(1, TriggerOf(s));

        await s.Set(3);
        Assert.Equal(2, TriggerOf(s));
    }

    [Fact]
    public async Task Signal_SetToSameValue_DoesNotBumpTrigger()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);

        await s.Set(1);
        Assert.Equal(0, TriggerOf(s));

        await s.Set(2);
        await s.Set(2);
        Assert.Equal(1, TriggerOf(s));
    }

    [Fact]
    public async Task EagerRelativeSignal_EverySet_BumpsTrigger()
    {
        var f = new MemoFactory();
        var s = f.CreateEagerRelativeSignal(1);

        // A relative Set always propagates CacheDirty (no equality short-cut), so the trigger
        // bumps even when fn returns the same value.
        await s.Set(v => v + 1);
        await s.Set(v => v);
        Assert.Equal(2, TriggerOf(s));
    }

    [Fact]
    public void Memo_BeforeFirstEvaluation_HasEmptyStamp()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await s.Get());

        Assert.Equal(CausalityStamp.Empty, m.Stamp);
        Assert.Empty(m.SourceStamps);
    }

    [Fact]
    public async Task Memo_StampJoinsSourceStamps_AndKeepsAStampPerSource()
    {
        var f = new MemoFactory();
        var s1 = f.CreateSignal(1);
        var s2 = f.CreateSignal(2);
        await s1.Set(2);
        await s1.Set(3); // trigger 2
        await s2.Set(5); // trigger 1

        var inner = f.CreateMemoizR(async () => await s2.Get() * 2);
        var outer = f.CreateMemoizR(async () => await s1.Get() + await inner.Get());

        Assert.Equal(13, await outer.Get());

        // A memo source contributes its full transitive signal map, a signal source its single
        // entry -- the node's own stamp is the join (the issue's notation).
        Assert.Equal(Stamp((s2, 1)), inner.Stamp);
        Assert.Equal(Stamp((s1, 2), (s2, 1)), outer.Stamp);

        // "Every Node keeps a Stamp for each of its Sources", keyed by source id.
        Assert.Equal(2, outer.SourceStamps.Count);
        Assert.Equal(Stamp((s1, 2)), outer.SourceStamps[s1.Id]);
        Assert.Equal(Stamp((s2, 1)), outer.SourceStamps[inner.Id]);
    }

    [Fact]
    public async Task Issue39_DiamondExample_ReproducesIssueStamps()
    {
        // The exact graph from issue #39:
        //   Signal #1 (trigger 4) --> Memo #5 --> Reaction #7
        //   Signal #2 (trigger 7) --> Memo #4 --> Memo #5
        //   Signal #3 (trigger 2) --> Memo #4 --> Memo #6 --> Reaction #7
        var f = new MemoFactory();
        var s1 = f.CreateSignal("s1", 0);
        var s2 = f.CreateSignal("s2", 0);
        var s3 = f.CreateSignal("s3", 0);

        for (var i = 1; i <= 4; i++) await s1.Set(i);
        for (var i = 1; i <= 7; i++) await s2.Set(i);
        for (var i = 1; i <= 2; i++) await s3.Set(i);

        var m4 = f.CreateMemoizR("m4", async () => await s2.Get() + await s3.Get());
        var m5 = f.CreateMemoizR("m5", async () => await s1.Get() + await m4.Get());
        var m6 = f.CreateMemoizR("m6", async () => await m4.Get());

        Assert.Equal(13, await m5.Get());
        Assert.Equal(9, await m6.Get());

        var expectedM4 = Stamp((s2, 7), (s3, 2));
        var expectedM5 = Stamp((s1, 4), (s2, 7), (s3, 2));

        Assert.Equal(expectedM4, m4.Stamp);
        Assert.Equal(expectedM5, m5.Stamp);
        Assert.Equal(expectedM4, m6.Stamp);

        // Per-source stamps as in the issue's edge labels.
        Assert.Equal(Stamp((s1, 4)), m5.SourceStamps[s1.Id]);
        Assert.Equal(expectedM4, m5.SourceStamps[m4.Id]);
        Assert.Equal(expectedM4, m6.SourceStamps[m4.Id]);

        // The glitch-freedom check across the reaction's inputs: both reflect the same signal
        // versions on their shared causality.
        Assert.True(m5.Stamp.IsConsistentWith(m6.Stamp));

        var r7 = f.BuildReaction("r7").CreateReaction(m5, m6, (a, b) => { });
        await TestHelpers.WaitForConvergenceAsync(() => expectedM5.Equals(r7.Stamp));
        Assert.Equal(expectedM5, r7.Stamp);
        Assert.Equal(expectedM5, r7.SourceStamps[m5.Id]);
        Assert.Equal(expectedM4, r7.SourceStamps[m6.Id]);
    }

    [Fact]
    public async Task Memo_DynamicRewiring_DropsDroppedSignalFromStamp()
    {
        var f = new MemoFactory();
        var cond = f.CreateSignal(true);
        var a = f.CreateSignal(10);
        var b = f.CreateSignal(20);
        var m = f.CreateMemoizR(async () => await cond.Get() ? await a.Get() : await b.Get());

        Assert.Equal(10, await m.Get());
        Assert.Equal(Stamp((cond, 0), (a, 0)), m.Stamp);
        Assert.False(m.Stamp.TryGetTrigger(b.Id, out _));

        await cond.Set(false);
        Assert.Equal(20, await m.Get());

        // The dropped branch's signal must leave the stamp, or a distributed consistency check
        // would keep comparing against a version the value no longer depends on.
        Assert.Equal(Stamp((cond, 1), (b, 0)), m.Stamp);
        Assert.False(m.Stamp.TryGetTrigger(a.Id, out _));
        Assert.False(m.SourceStamps.ContainsKey(a.Id));
    }

    [Fact]
    public async Task Memo_SkippedRecompute_KeepsOlderStamp_NeverOverclaims()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(4);
        var p = f.CreateMemoizR(async () => await s.Get() / 2);
        var c = f.CreateMemoizR(async () => await p.Get() * 10);

        Assert.Equal(20, await c.Get());
        Assert.Equal(Stamp((s, 0)), c.Stamp);

        // s: 4 -> 5 bumps the trigger, but p recomputes to the same value (5/2 == 2), so c's
        // CacheCheck resolves without recomputing: c keeps the OLDER stamp. Under-claiming is
        // the safe direction -- the stamp must never claim a write the value was not computed
        // from -- at the cost of a conservative (spurious) inconsistency against p.
        await s.Set(5);
        Assert.Equal(20, await c.Get());
        Assert.Equal(Stamp((s, 1)), p.Stamp);
        Assert.Equal(Stamp((s, 0)), c.Stamp);
        Assert.False(c.Stamp.IsConsistentWith(p.Stamp));

        // A change that propagates re-aligns the stamps.
        await s.Set(6);
        Assert.Equal(30, await c.Get());
        Assert.Equal(Stamp((s, 2)), c.Stamp);
        Assert.True(c.Stamp.IsConsistentWith(p.Stamp));
    }

    [Fact]
    public async Task Memo_RereadingSameSignal_ProducesSingleEntry()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(3);
        var m = f.CreateMemoizR(async () => await s.Get() + await s.Get());

        Assert.Equal(6, await m.Get());
        Assert.Equal(Stamp((s, 0)), m.Stamp);
        Assert.Single(m.SourceStamps);
    }

    [Fact]
    public async Task Untracked_ReadsAreNotStamped()
    {
        var f = new MemoFactory();
        var s1 = f.CreateSignal(1);
        var s2 = f.CreateSignal(2);
        var m = f.CreateMemoizR(async () => await s1.Get() + await f.Untrack(async () => await s2.Get()));

        Assert.Equal(3, await m.Get());

        // Untracked reads are deliberately invisible to the dependency graph; the stamp mirrors
        // the graph, not the data flow.
        Assert.Equal(Stamp((s1, 0)), m.Stamp);
        Assert.False(m.Stamp.TryGetTrigger(s2.Id, out _));
    }

    [Fact]
    public async Task Reaction_CapturesSourceStamps()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        var r = f.BuildReaction().CreateReaction(s, _ => { });

        await TestHelpers.WaitForConvergenceAsync(() => Stamp((s, 0)).Equals(r.Stamp));
        Assert.Equal(Stamp((s, 0)), r.Stamp);

        await s.Set(2);

        await TestHelpers.WaitForConvergenceAsync(() => Stamp((s, 1)).Equals(r.Stamp));
        Assert.Equal(Stamp((s, 1)), r.Stamp);
        Assert.Equal(Stamp((s, 1)), r.SourceStamps[s.Id]);
    }

    [Fact]
    public async Task ConcurrentMapReduce_StampJoinsAllBranchReads()
    {
        var f = new MemoFactory();
        var s1 = f.CreateSignal(1);
        var s2 = f.CreateSignal(2);
        await s1.Set(3); // trigger 1

        var cmr = f.CreateConcurrentMapReduce(
            async _ => await s1.Get(),
            async _ => await s2.Get());

        Assert.Equal(5, await cmr.Get());
        Assert.Equal(Stamp((s1, 1), (s2, 0)), cmr.Stamp);
        Assert.Equal(Stamp((s1, 1)), cmr.SourceStamps[s1.Id]);
        Assert.Equal(Stamp((s2, 0)), cmr.SourceStamps[s2.Id]);
    }

    [Fact]
    public async Task ConcurrentMap_StampJoinsAllBranchReads()
    {
        var f = new MemoFactory();
        var s1 = f.CreateSignal(1);
        var s2 = f.CreateSignal(2);

        var cm = f.CreateConcurrentMap(
            async _ => await s1.Get(),
            async _ => await s2.Get());

        Assert.Equal([1, 2], await cm.Get());

        // The mapped branches run on their own forced scopes, but they evaluate on this node's
        // behalf -- the capture is keyed by node, so the stamps land here all the same.
        Assert.Equal(Stamp((s1, 0), (s2, 0)), cm.Stamp);
    }

    [Fact]
    public async Task ConcurrentRace_StampReflectsTrackedReads()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(5);
        await s.Set(6); // trigger 1

        var race = f.CreateConcurrentRace<int, int>(
            async () => await s.Get(),
            async (_, input) => input * 2);

        Assert.Equal(12, await race.Get());
        Assert.Equal(Stamp((s, 1)), race.Stamp);
    }

    [Fact]
    public async Task Signal_ValueAndStampPair_IsNeverTorn()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);

        // The writer publishes value i together with trigger i in one box swap, so EVERY
        // observable (value, stamp) pair must satisfy trigger == value -- a reader pairing a new
        // value with an old stamp (or vice versa) fails immediately.
        var stop = false;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var (value, stamp) = s.ValueAndStamp;
                Assert.True(stamp.TryGetTrigger(s.Id, out var trigger));
                Assert.Equal(value, trigger);
            }
        })).ToArray();

        for (var i = 1; i <= 2000; i++)
        {
            await s.Set(i);
        }

        Volatile.Write(ref stop, true);
        await Task.WhenAll(readers);

        var (finalValue, finalStamp) = s.ValueAndStamp;
        Assert.True(finalStamp.TryGetTrigger(s.Id, out var finalTrigger));
        Assert.Equal(2000, finalValue);
        Assert.Equal(2000, finalTrigger);
    }

    [Fact]
    public async Task Signal_ConcurrentDistinctSets_CountEveryChange()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);

        // Globally distinct values: no Set can hit the value-unchanged short-cut, so each of
        // the 1000 Sets must bump the trigger exactly once despite racing writer flows.
        const int writers = 4;
        const int setsPerWriter = 250;
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 1; i <= setsPerWriter; i++)
            {
                await s.Set(w * 10000 + i);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(writers * setsPerWriter, TriggerOf(s));
    }
}
