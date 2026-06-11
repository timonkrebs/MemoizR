using MemoizR.StructuredAsyncLock;
using MemoizR.StructuredConcurrency;

namespace MemoizR.Tests;

// Pins for the bugs fixed in the max-effort review of the concurrency-hardening branch. Each
// test names the failure mode it guards; most failed deterministically (or near-) against the
// pre-fix code.
public class ConcurrencyHardeningTests
{
    // AccumulateSourcesAndObservers used to OVERWRITE the shared accumulator whenever a child's
    // CurrentGetsIndex was 0 -- always true for ConcurrentMap's forced per-child scopes -- so the
    // map's Sources held only the last-finishing child's captures. A Set under any other child's
    // memo then left the map parked at CacheCheck, the parent scan found nothing dirty, and Get
    // served the stale sequence forever.
    [Fact(Timeout = 10000)]
    public async Task ConcurrentMap_MultipleMemoSources_InvalidateThroughEveryChild()
    {
        var f = new MemoFactory();
        var s1 = f.CreateSignal(1);
        var s2 = f.CreateSignal(10);
        var m1 = f.CreateMemoizR(async () => await s1.Get() * 2);
        var m2 = f.CreateMemoizR(async () => await s2.Get() * 2);
        var map = f.CreateConcurrentMap(
            async _ => await m1.Get(),
            async _ => await m2.Get());

        Assert.Equal([2, 20], await map.Get());

        await s1.Set(5); // first child's dependency chain
        Assert.Equal([10, 20], await map.Get());

        await s2.Set(50); // second child's dependency chain
        Assert.Equal([10, 100], await map.Get());
    }

    // On a re-run whose reads all prefix-match the previous Sources, the accumulator gathered
    // NOTHING (prefix matches leave CurrentGets empty), so HandleSubscriptions wiped the node's
    // Sources to []. The next invalidation arrived as CacheCheck, the empty parent scan resolved
    // it to Clean, and the node cached the second run's value forever.
    [Fact(Timeout = 10000)]
    public async Task ConcurrentMapReduce_RerunWithUnchangedDependencies_KeepsSourceLinks()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await s.Get() * 2);
        var cmr = f.CreateConcurrentMapReduce(async _ => await m.Get());

        Assert.Equal(2, await cmr.Get());

        await s.Set(2);
        Assert.Equal(4, await cmr.Get()); // re-run: every read prefix-matches

        await s.Set(3); // pre-fix: Sources were wiped by the re-run -> stale 4 forever
        Assert.Equal(6, await cmr.Get());
    }

    // ComputeAsync used to expose the job's ConcurrentDictionary through a lazy Select: bucket
    // enumeration order, not fns order. With more fns than the dictionary's default bucket count
    // the results interleaved (0, 31, 1, 32, ...). They must come back in fns order.
    [Fact(Timeout = 10000)]
    public async Task ConcurrentMap_ReturnsResultsInFnOrder()
    {
        var f = new MemoFactory();
        var fns = Enumerable.Range(0, 40)
            .Select(i => (Func<IStructuredResourceGroup, Task<int>>)(async _ =>
            {
                await Task.Delay(i % 4); // stagger completions so arrival order differs from index order
                return i;
            }))
            .ToArray();
        var map = f.CreateConcurrentMap(fns);

        Assert.Equal(Enumerable.Range(0, 40).ToArray(), (await map.Get()).ToArray());
    }

    // ReleaseExclusiveLock zeroed lockScope whenever locksHeld hit 0, even while a same-scope
    // recursive upgradeable was still held. The surviving holder's next same-flow acquisition
    // then failed the scope comparison and enqueued behind itself -- a deadlock that wedged the
    // flow's ContextLock for good.
    [Fact(Timeout = 5000)]
    public async Task ExclusiveRelease_KeepsLockScope_WhileRecursiveUpgradeableHeld()
    {
        var asyncLock = new AsyncAsymmetricLock();

        var exclusive = await asyncLock.ExclusiveLockAsync();
        var upgradeable = await asyncLock.UpgradeableLockAsync(); // same-flow recursive grant

        exclusive.Dispose();
        Assert.NotEqual(0, asyncLock.LockScope); // the scope still owns the upgradeable

        // The still-held upgradeable's flow must still be recognized as the owner: a same-flow
        // re-acquisition is granted synchronously instead of queueing behind itself.
        var nested = asyncLock.UpgradeableLockAsync();
        Assert.True(nested.IsCompletedSuccessfully, "same-flow re-acquisition queued behind its own held lock");
        (await nested).Dispose();

        upgradeable.Dispose();
        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }

    // Debounced updates are detached tasks that INHERIT the triggering Set's AsyncLocal flow.
    // Before they forced their own scope, an update sharing a pinned flow was granted that flow's
    // ContextLock as a "recursive" acquisition -- so a later Set on the flow could race the
    // update and die with "Can not aquire recursive exclusive lock in the scope of an
    // upgradeable lock". Sequential awaited Sets on a pinned flow must never throw.
    [Fact(Timeout = 20000)]
    public async Task ZeroDebounceReaction_SequentialSetsOnPinnedFlow_DoNotThrow()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);
        var last = -1;
        var r = f.BuildReaction().AddDebounceTime(TimeSpan.Zero).CreateReaction(s, v => last = v);

        f.Context.GetOrCreateScope(); // pin THIS flow: the updates would inherit it

        for (var i = 1; i <= 25; i++)
        {
            await s.Set(i); // pre-fix: raced the in-flight update's upgradeable -> InvalidOperationException
        }

        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref last) == 25);
        Assert.Equal(25, last);
        GC.KeepAlive(r);
    }

    // The catch-path rewire treated an aborted run's EMPTY capture as authoritative: a re-run
    // that threw before its first read stripped every source/observer link, so no later Set
    // could ever revive the reaction. A failed RE-run must keep the previous links.
    [Fact(Timeout = 10000)]
    public async Task Reaction_RerunThrowsBeforeFirstRead_KeepsLinks_AndRecovers()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        var last = -1;
        var attempts = 0;
        var fail = false;
        var r = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            Interlocked.Increment(ref attempts);
            if (Volatile.Read(ref fail)) throw new InvalidOperationException("boom before any read");
            last = await s.Get();
        });

        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref last) == 1); // first run wired s
        Assert.Equal(1, last);

        Volatile.Write(ref fail, true);
        var attemptsBefore = Volatile.Read(ref attempts);
        await s.Set(2); // re-run throws BEFORE reading s
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref attempts) > attemptsBefore);

        Volatile.Write(ref fail, false);
        await s.Set(3); // pre-fix: the link was stripped, nothing ever triggered again
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref last) == 3);
        Assert.Equal(3, last);
        GC.KeepAlive(r);
    }

    // With a zero debounce, Task.Delay completes synchronously and the "fire-and-forget" initial
    // run used to execute the ENTIRE update -- including the user's body -- inline inside the
    // construction path (under the reaction's scheduling monitor AND the builder's factory
    // lock). A body that blocks synchronously then deadlocked the constructor. Creation must
    // return while the body runs elsewhere.
    [Fact(Timeout = 10000)]
    public async Task ReactionCreation_ReturnsWhileInitialRunStillExecuting()
    {
        var f = new MemoFactory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var r = f.BuildReaction().CreateAdvancedReaction(() =>
        {
            entered.TrySetResult();
            release.Task.Wait(); // blocks synchronously: inline execution would hang the ctor
            return Task.CompletedTask;
        });

        // Pre-fix this point was unreachable (the constructor never returned); the timeout fired.
        await entered.Task;
        release.SetResult();
        GC.KeepAlive(r);
    }

    // After a FAILED first computation the node was forced to CacheCheck; with no sources wired
    // yet, the next Get's parent scan resolved to Clean and served default(T) -- fabricated data,
    // no error, no retry. Until a computation succeeds, every Get must retry and surface the
    // failure.
    [Fact(Timeout = 10000)]
    public async Task Memo_FirstComputationThrows_RetriesOnEveryGet_AndSurfaces()
    {
        var f = new MemoFactory();
        var fail = true;
        var m = f.CreateMemoizR<int>(async () =>
        {
            await Task.Yield();
            if (Volatile.Read(ref fail)) throw new InvalidOperationException("first boom");
            return 42;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Get());
        // Pre-fix: this second Get returned 0 (default) as CacheClean instead of throwing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Get());

        Volatile.Write(ref fail, false);
        Assert.Equal(42, await m.Get());
        Assert.Equal(42, await m.Get()); // and the success commits Clean normally
    }

    // The chain variant: a child whose parent has never computed successfully must keep
    // surfacing the parent's failure instead of silently caching a value derived from defaults.
    [Fact(Timeout = 10000)]
    public async Task MemoChain_FirstComputationThrows_KeepsSurfacing_UntilInputFixed()
    {
        var f = new MemoFactory();
        var fail = true;
        var p = f.CreateMemoizR<int>(async () =>
        {
            await Task.Yield();
            return Volatile.Read(ref fail) ? throw new InvalidOperationException("p boom") : 21;
        });
        var c = f.CreateMemoizR(async () => await p.Get() + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Get());
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Get());

        Volatile.Write(ref fail, false);
        Assert.Equal(22, await c.Get());
    }

    // An equal-value Set changes nothing in the graph, so it must not notify observers at all.
    // It used to propagate CacheCheck (bumping observer generations): under an equal-value write
    // storm, an in-flight recompute's commit was refused every cycle -- commit starvation -- and
    // every observer was needlessly knocked out of Clean.
    [Fact(Timeout = 10000)]
    public async Task Signal_EqualValueSet_DoesNotNotifyObservers()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(5);
        var invocations = 0;
        var m = f.CreateMemoizR(async () =>
        {
            Interlocked.Increment(ref invocations);
            return await s.Get();
        });

        Assert.Equal(5, await m.Get());
        var before = invocations;

        for (var i = 0; i < 10; i++)
        {
            await s.Set(5);
        }

        // The memo must still be CLEAN -- not parked at CacheCheck by a pointless re-verify.
        Assert.Equal(CacheState.CacheClean, ((IMemoizR)m).State);
        Assert.Equal(5, await m.Get());
        Assert.Equal(before, invocations);
    }

    // Observer membership is mutated from three different lock domains; the add/remove must be
    // atomic per node or concurrent whole-array swaps silently drop subscriptions (a missed
    // trigger with no recovery path). Hammer the new primitives directly.
    [Fact(Timeout = 10000)]
    public async Task Observers_AddRemove_AreAtomic_UnderConcurrentMutation()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);
        var keep = Enumerable.Range(0, 32).Select(_ => f.CreateMemoizR(async () => 0)).ToArray();
        var drop = Enumerable.Range(0, 32).Select(_ => f.CreateMemoizR(async () => 0)).ToArray();
        var source = (IMemoHandlR)s;

        foreach (var m in drop)
        {
            source.AddObserver(m);
        }

        // Concurrent adds (all keepers) and removes (all droppers) against one node.
        await Task.WhenAll(
            keep.Select(m => Task.Run(() => source.AddObserver(m)))
                .Concat(drop.Select(m => Task.Run(() => source.RemoveObserver(m)))));

        Assert.Equal(32, s.Observers.Length);
        foreach (var m in keep)
        {
            Assert.True(TestHelpers.Observes(s.Observers, m), "a concurrent add was lost");
        }
        foreach (var m in drop)
        {
            Assert.False(TestHelpers.Observes(s.Observers, m), "a concurrent remove was lost");
        }
        GC.KeepAlive(keep);
    }

    // Add-if-absent: re-adding an existing observer must not create a duplicate entry (duplicates
    // mean duplicate Stale propagation per Set, growing without bound across job re-runs).
    [Fact(Timeout = 5000)]
    public async Task Observers_Add_IsIdempotent()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);
        var m = f.CreateMemoizR(async () => 0);
        var source = (IMemoHandlR)s;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => source.AddObserver(m))));

        Assert.Equal(1, s.Observers.Length);
        GC.KeepAlive(m);
    }

    // Untrack on an unpinned flow is a no-op by construction (a throwaway scope's CurrentReaction
    // is always null); it must not mint/register scopes, and on a pinned flow the listener must
    // be restored onto the SAME scope instance that was nulled.
    [Fact(Timeout = 5000)]
    public async Task Untrack_RunsBody_WithoutLeakingScopes_AndRestoresListener()
    {
        var f = new MemoFactory();

        Assert.Equal(42, f.Untrack(() => 42));
        Assert.Equal(43, await f.Untrack(async () => { await Task.Yield(); return 43; }));
        Assert.Equal(0, f.Context.RegisteredScopeCount); // unpinned flow: nothing registered

        var scope = f.Context.GetOrCreateScope(); // pin this flow
        var m = f.CreateMemoizR(async () => 1);
        scope.CurrentReaction = m;

        f.Untrack(() =>
        {
            Assert.Null(f.Context.ReactionScope.CurrentReaction); // capture suspended inside
            return 0;
        });

        Assert.Same(m, scope.CurrentReaction); // restored onto the same instance
        GC.KeepAlive(scope);
    }
}
