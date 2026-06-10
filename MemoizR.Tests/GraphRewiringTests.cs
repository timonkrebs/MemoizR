using System.Runtime.CompilerServices;
using xRetry;

namespace MemoizR.Tests;

// Contracts of the dynamic graph rewiring (SignalHandlR.UpdateSourceAndObserverLinks and the
// observer down-links): dependency narrowing, shared-prefix tail switching, dead-observer
// pruning, and the Equals-based diamond guard. This is the subtlest non-concurrent logic in the
// codebase and now lives in one shared base-class method, so one test covers every node type.
public class GraphRewiringTests
{
    [Fact(Timeout = 10000)]
    public async Task Memo_StopsObservingDroppedSource()
    {
        var f = new MemoFactory();
        var useB = f.CreateSignal(true);
        var a = f.CreateSignal(1);
        var b = f.CreateSignal(10);
        var invocations = 0;
        var m = f.CreateMemoizR(async () =>
        {
            invocations++;
            return await useB.Get() ? await a.Get() + await b.Get() : await a.Get();
        });

        Assert.Equal(11, await m.Get()); // Sources: [useB, a, b]
        Assert.True(TestHelpers.Observes(b.Observers, m), "m should be observing b");

        // Narrow the dependency set: the recompute reads only the [useB, a] prefix, so the
        // else-branch of UpdateSourceAndObserverLinks must truncate Sources and unsubscribe b.
        await useB.Set(false);
        Assert.Equal(1, await m.Get());
        var afterNarrowing = invocations;

        Assert.False(TestHelpers.Observes(b.Observers, m), "m should no longer be observing b");

        // A write to the dropped source must neither dirty m nor recompute it.
        await b.Set(99);
        Assert.Equal(1, await m.Get());
        Assert.Equal(afterNarrowing, invocations);

        // The kept prefix is still live.
        await a.Set(2);
        Assert.Equal(2, await m.Get());
    }

    [Fact(Timeout = 10000)]
    public async Task Memo_SwitchesTailSource_KeepsSharedPrefix()
    {
        var f = new MemoFactory();
        var a = f.CreateSignal(1);     // shared prefix
        var pick = f.CreateSignal(true);
        var b = f.CreateSignal(10);
        var c = f.CreateSignal(100);
        var m = f.CreateMemoizR(async () =>
            await a.Get() + (await pick.Get() ? await b.Get() : await c.Get()));

        Assert.Equal(11, await m.Get()); // reads [a, pick, b]

        // Switch the tail: the recompute reads [a, pick, c]. CheckDependenciesTheSame matches
        // the [a, pick] prefix in place, so the merge branch (Sources.Take(index) + CurrentGets)
        // runs: b must be unsubscribed, c subscribed, a and pick untouched.
        await pick.Set(false);
        Assert.Equal(101, await m.Get());

        Assert.False(TestHelpers.Observes(b.Observers, m), "m should no longer be observing b");
        Assert.True(TestHelpers.Observes(c.Observers, m), "m should be observing c");

        await b.Set(999);              // dropped source: must not dirty m
        Assert.Equal(101, await m.Get());

        await c.Set(200);              // new tail source: must recompute
        Assert.Equal(201, await m.Get());

        await a.Set(2);                // shared prefix source: still wired
        Assert.Equal(202, await m.Get());
    }

    // A collected observer (dead WeakReference in a source's Observers) must be skipped by the
    // invalidation cascade without breaking propagation to the live observers. Depends on the GC
    // actually collecting the dropped memo, so retried like TestAutoSubscriptionHandling.
    [RetryFact(3, 200)]
    public async Task CollectedMemoObserver_IsPruned_WithoutBreakingCascade()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var live = f.CreateMemoizR(async () => await v.Get() * 2);
        Assert.Equal(2, await live.Get());

        await CreateAndDropMemo(f, v);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The cascade walks a dead weak reference and must skip it, still reaching `live`.
        await v.Set(5);
        Assert.Equal(10, await live.Get());
        GC.KeepAlive(live);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CreateAndDropMemo(MemoFactory f, Signal<int> v)
    {
        var dead = f.CreateMemoizR(async () => await v.Get() + 1);
        Assert.Equal(2, await dead.Get()); // wires v.Observers -> dead, then drops the only strong ref
    }

    private sealed record Boxed(int V); // record: value equality, distinct instances

    // The diamond down-link is gated on !Equals(oldValue, Value): a recompute that produces an
    // equal-but-not-same instance must NOT dirty observers -- pins the no-redundant-propagation
    // contract for non-primitive value semantics.
    [Fact(Timeout = 10000)]
    public async Task Memo_EqualButNotSameRecomputedValue_DoesNotDirtyObservers()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        var parentInvocations = 0;
        var childInvocations = 0;
        var parent = f.CreateMemoizR(async () =>
        {
            parentInvocations++;
            return new Boxed(await s.Get() / 2);
        });
        var child = f.CreateMemoizR(async () =>
        {
            childInvocations++;
            return (await parent.Get()).V;
        });

        Assert.Equal(0, await child.Get()); // s=1 -> parent Boxed(0) -> child 0
        var childRuns = childInvocations;

        await s.Set(0);                     // parent recomputes: a NEW Boxed(0), Equals the old one
        Assert.Equal(0, await child.Get());

        Assert.True(parentInvocations >= 2, "the parent must have recomputed");
        Assert.Equal(childRuns, childInvocations); // the child must not have
    }
}
