using System.Linq;
using xRetry;
using MemoizR.StructuredAsyncLock;
using MemoizR.StructuredConcurrency;

namespace MemoizR.Tests;

// Regression tests covering bugs fixed while stabilising the flaky test suite.
public class RegressionTests
{
    // The lock must hand back a plain Task<IDisposable>. It previously returned the custom
    // AwaitableDisposable awaitable, which Coyote's binary rewriter could not resolve
    // (MissingMethodException broke every lock-using test under `coyote rewrite`).
    // This compiles only while the return type stays assignable to Task<IDisposable>.
    [Fact(Timeout = 1000)]
    public async Task LockMethodsReturnPlainTask()
    {
        var asyncLock = new AsyncAsymmetricLock();

        Task<IDisposable> exclusive = asyncLock.ExclusiveLockAsync();
        (await exclusive).Dispose();

        Task<IDisposable> upgradeable = asyncLock.UpgradeableLockAsync();
        (await upgradeable).Dispose();

        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }

    // Scope keys are generated with a thread-safe RNG (Random.Shared). Previously a static
    // `new Random()` shared across all contexts but guarded only by a per-instance lock could
    // be corrupted by concurrent access and hand out colliding/zero scope keys. Many contexts
    // built and exercised in parallel must each compute correctly.
    [Fact(Timeout = 30000)]
    public async Task ParallelFactoriesDoNotCorruptScopes()
    {
        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var f = new MemoFactory();
            var v = f.CreateSignal(i);
            var m = f.CreateMemoizR(async () => await v.Get() * 2);

            Assert.Equal(i * 2, await m.Get());
            await v.Set(i + 1);
            Assert.Equal((i + 1) * 2, await m.Get());
        }));

        await Task.WhenAll(tasks);
    }

    // A failing structured job must surface an AggregateException that still contains the
    // underlying fault. The catch path previously relied on ConfigureAwaitOptions.SuppressThrowing,
    // which the Coyote rewriter does not honour, collapsing it to a single bare exception.
    [Fact(Timeout = 5000)]
    public async Task FailingJobThrowsAggregateContainingFault()
    {
        var f = new MemoFactory();
        var c1 = f.CreateConcurrentMapReduce(
            (IStructuredResourceGroup c) => Task.FromException<int>(new InvalidOperationException("boom")),
            async c => { await Task.Delay(50, c.Token); return 1; });

        var ex = await Assert.ThrowsAsync<AggregateException>(c1.Get);
        Assert.Contains(ex.Flatten().InnerExceptions, e => e.Message == "boom");
    }

    // Rapid updates inside the debounce window must collapse to a single reaction run with the
    // final value. The previous debounce used Task.Delay(...).ContinueWith, which ran even when
    // the delay was cancelled, so superseded updates were not actually dropped.
    [RetryFact(3, 200)]
    public async Task DebounceCoalescesRapidUpdates()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(0);
        var invocations = 0;
        var last = -1;

        f.BuildReaction()
            .AddDebounceTime(TimeSpan.FromMilliseconds(50))
            .CreateReaction(v1, v => { Interlocked.Increment(ref invocations); last = v; });

        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref invocations) >= 1);
        var afterInitial = invocations; // the initial reaction run

        for (var i = 1; i <= 10; i++)
        {
            await v1.Set(i);
        }

        // Wait for the coalesced run to land on the final value, then keep a fixed quiescence
        // window (4x the 50ms debounce) for the negative half: no FURTHER invocation may follow.
        await TestHelpers.WaitForConvergenceAsync(() => last == 10);
        await Task.Delay(200);

        Assert.Equal(10, last);
        Assert.Equal(afterInitial + 1, invocations);
    }

    // The lock-free Get fast path reads State (volatile) and the cached Value without taking the
    // ContextLock. Hammer that path from many readers while a writer keeps changing the source:
    // readers must never observe an invalid value and the memo must converge to the last write.
    // (A memory-visibility regression can't be proven by a unit test, but this guards the logic
    // and exercises the fast path heavily.)
    [Fact(Timeout = 20000)]
    public async Task Memo_ConcurrentFastPathReads_StayConsistentAndConverge()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(0);
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);
        await m1.Get(); // prime so the CacheClean fast path is exercised

        using var cts = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            // Writes are serialized (sequential awaited Sets recomputed under the node mutex), so
            // the published values are monotonically increasing; a reader observing a smaller
            // value after a larger one would mean the fast path returned a stale snapshot. (An
            // int read can't tear, so an "is even" style check could never fail.)
            var lastSeen = 0;
            while (!cts.IsCancellationRequested)
            {
                var r = await m1.Get();
                Assert.True(r >= lastSeen, $"fast path went backwards: saw {r} after {lastSeen}");
                lastSeen = r;
                // Yield so 8 readers don't pin the whole thread pool (the CacheClean fast path
                // completes synchronously) and starve the writer's continuations on small CI runners.
                await Task.Yield();
            }
        })).ToArray();

        for (var i = 1; i <= 500; i++)
        {
            await v1.Set(i);
        }

        cts.Cancel();
        await Task.WhenAll(readers);

        Assert.Equal(1000, await m1.Get()); // converged to the last write (500 * 2)
    }

    // A value type wider than a machine word whose two halves are always written equal. The memo
    // value is set as one coherent struct; a torn read on the lock-free Get fast path (a writer
    // overwriting Value while a reader copies it) would surface mismatched halves. Backing Value
    // with an immutable box behind a single volatile reference makes that impossible -- the reader
    // only ever observes a fully-constructed box. This test guards that no-tearing invariant; it
    // does not assert convergence *during* the concurrent reads, because whether a memo read by
    // many independent flows converges immediately is governed by the (separate) per-flow
    // ContextLock behaviour, not by the value publication this test covers.
    private readonly record struct Pair(long A, long B);

    [Fact(Timeout = 20000)]
    public async Task Memo_ConcurrentFastPathReads_NeverTearWideStruct()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(0L);
        var m1 = f.CreateMemoizR(async () =>
        {
            var x = await v1.Get();
            return new Pair(x, x);
        });
        await m1.Get(); // prime so the CacheClean fast path is exercised

        using var cts = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var p = await m1.Get();
                Assert.True(p.A == p.B, $"torn read: halves came from different writes ({p.A} != {p.B})");
                // Yield so 8 readers don't pin the whole thread pool (the CacheClean fast path
                // completes synchronously) and starve the writer's continuations on small CI runners.
                await Task.Yield();
            }
        })).ToArray();

        for (var i = 1L; i <= 2000; i++)
        {
            await v1.Set(i);
        }

        cts.Cancel();
        await Task.WhenAll(readers);

        // Once all concurrency has stopped, a fresh write must still propagate cleanly -- this
        // also confirms the fast path returns the recomputed value, not a stale snapshot.
        await v1.Set(123456);
        Assert.Equal(new Pair(123456, 123456), await m1.Get());
    }

    // Deterministic reproduction of the cross-flow lost-update race. A memo's recompute (Update)
    // ends by marking the node Clean. If a Stale (from a Set on another flow) dirties the node
    // *during* that recompute, the Clean write must not clobber the Dirty -- otherwise the memo
    // caches the value it computed from the now-stale source and never reconverges.
    // Gates pin the exact interleaving: the recompute reads the source, parks, a newer Set lands,
    // then the recompute resumes and tries to commit Clean.
    [Fact(Timeout = 10000)]
    public async Task Memo_StaleDuringRecompute_IsNotClobbered()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);

        var gate = new RecomputeGate();
        var m = f.CreateMemoizR(async () =>
        {
            var x = await s.Get();
            await gate.PauseIfArmedAsync();
            return x;
        });

        await m.Get();            // prime: m is Clean@0 and the s -> m dependency link is established
        await s.Set(1);           // m is now Dirty, s == 1

        gate.Arm();
        var getter = Task.Run(async () => await m.Get()); // recomputes on its own flow: reads s == 1, parks
        await gate.ReadDone;      // the recompute has read s == 1 and is parked mid-Update (Evaluating)

        await s.Set(2);           // invalidate during the parked recompute: m -> Dirty, s == 2

        gate.Proceed();           // resume the recompute; it will try to commit the stale value (1)
        await getter;

        // The Set(2) that landed during the recompute must win: the memo must reconverge to 2,
        // not stay stuck at the clobbered 1.
        Assert.Equal(2, await m.Get());
    }

    // Deterministic reproduction of the *suppressed*-Stale variant of the lost-update race.
    // Chain s -> p -> c with p = s/2, so p's value is the same for s=0 and s=1 (no diamond
    // down-link rescues c). While c's CacheCheck scan is parked inside p's recompute, a second
    // Set lands: its cascade reaches c as Stale(CacheCheck), but c is already CacheCheck, so the
    // state does not escalate. The generation must be bumped anyway -- if the suppressed Stale
    // leaves no trace, c's pending commit marks it Clean over a Dirty parent, and because the
    // cascade also stops at already-dirty nodes, nothing ever re-dirties c: it caches the stale
    // value forever.
    [Fact(Timeout = 10000)]
    public async Task Memo_SuppressedStaleDuringParentCheck_IsNotClobbered()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);

        var gate = new RecomputeGate();
        var p = f.CreateMemoizR(async () =>
        {
            var x = await s.Get();
            await gate.PauseIfArmedAsync();
            return x / 2; // same value for s=0 and s=1, changes only at s=2
        });
        var c = f.CreateMemoizR(async () => await p.Get());

        await c.Get();            // prime: s=0 -> p=0 -> c=0, links established
        await s.Set(1);           // cascade: p Dirty, c CacheCheck; p stays 0 once recomputed

        gate.Arm();
        var getter = Task.Run(async () => await c.Get()); // c's CacheCheck scan recomputes p, which parks
        await gate.ReadDone;      // p's recompute has read s == 1 and is parked

        await s.Set(2);           // cascade: p escalates (Evaluating -> Dirty), c's Stale(CacheCheck)
                                  // is suppressed (already CacheCheck) -- the generation bump is all
                                  // that protects c's pending commit

        gate.Proceed();           // p finishes with the unchanged value 0 -> no down-link to c;
                                  // c's scan sees no dirty parent and tries to commit Clean
        await getter;

        // s == 2 must win: p recomputes to 1 and c must follow, not stay cached at 0.
        Assert.Equal(1, await c.Get());
    }

    // Stress for the whole lost-update class across a multi-level chain: concurrent readers pull
    // recomputes through every level on their own flows while a writer keeps invalidating from
    // another, exercising the cascade-suppression, generation-bump and renotify paths at every
    // depth. Whatever interleavings occur, the chain must reflect the last write once quiescent,
    // and a fresh write afterwards must still propagate through every level (a node silently
    // stuck Clean-but-stale would fail both).
    [Fact(Timeout = 30000)]
    public async Task MemoChain_ConcurrentSetsAndReads_AlwaysReconverges()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);
        var m1 = f.CreateMemoizR(async () => await s.Get() * 2);
        var m2 = f.CreateMemoizR(async () => await m1.Get() + 1);
        var m3 = f.CreateMemoizR(async () => await m2.Get() * 3);
        await m3.Get(); // prime the whole chain

        using var cts = new CancellationTokenSource();
        var readers = new[] { (IStateGetR<int>)m1, m2, m3 }.Select(node => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await node.Get();
                await Task.Yield();
            }
        })).ToArray();

        for (var i = 1; i <= 300; i++)
        {
            await s.Set(i);
        }

        cts.Cancel();
        await Task.WhenAll(readers);

        // Quiescent: every level must serve the value derived from the last write.
        Assert.Equal((300 * 2 + 1) * 3, await m3.Get());

        // And a write after quiescence must still reach the deepest node.
        await s.Set(1000);
        Assert.Equal((1000 * 2 + 1) * 3, await m3.Get());
    }

    // Error-path contract: a throwing computation must surface to the caller, must not poison the
    // node, and must not sever its dependency links. Documented current semantics: with no
    // further invalidation the memo keeps serving the last good value (it does not retry the
    // failed computation on its own); the next write recomputes and recovers.
    [Fact(Timeout = 10000)]
    public async Task Memo_TransientException_DoesNotPoisonNode()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () =>
        {
            var x = await v1.Get();
            if (x == 13) throw new InvalidOperationException("boom13");
            return x * 2;
        });

        Assert.Equal(2, await m.Get());

        await v1.Set(13);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => m.Get());
        Assert.Contains("boom13", ex.Message);

        // No further invalidation: the memo serves the last good value rather than rethrowing
        // or returning default.
        Assert.Equal(2, await m.Get());

        // The failed evaluation must not have unhooked the memo from its source: a new write
        // still dirties it and the recompute succeeds.
        await v1.Set(7);
        Assert.Equal(14, await m.Get());
    }
}
