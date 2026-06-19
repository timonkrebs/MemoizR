namespace MemoizR.Tests;

// Contracts of the experimental actor engine (issue #36 layer 5, ADR 0006), deliberately
// mirroring the lock-based engine's invariants: lazy memoization with a lock-free clean fast
// path, push-pull invalidation, generation-guarded commits (the deterministic I2/I3 races are
// ported below), diamond absorption with a single recompute, dynamic rewiring, at most one
// evaluation per node (waiters instead of a node mutex), parallel computation of independent
// nodes (the actor serializes bookkeeping turns, never user computations), cycle detection by
// flow identity, and the flow-side rejection of Set inside a computation.
public class ActorEngineTests
{
    [Fact(Timeout = 10000)]
    public async Task Memoization_IsLazy_AndCachesUntilInvalidated()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(1);
        var computeCount = 0;
        var m = f.CreateActorMemoizR(async () =>
        {
            Interlocked.Increment(ref computeCount);
            return await v.Get() * 2;
        });

        Assert.Equal(0, computeCount); // creation does not evaluate

        Assert.Equal(2, await m.Get());
        Assert.Equal(2, await m.Get()); // cached: clean fast path
        Assert.Equal(1, computeCount);

        await v.Set(2); // push marks; nothing evaluates
        Assert.Equal(1, computeCount);

        Assert.Equal(4, await m.Get()); // pull recomputes
        Assert.Equal(2, computeCount);
    }

    [Fact(Timeout = 10000)]
    public async Task EqualValueSet_TriggersReVerification_NotRecomputation()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(2);
        var computeCount = 0;
        var m = f.CreateActorMemoizR(async () =>
        {
            Interlocked.Increment(ref computeCount);
            return await v.Get() * 3;
        });

        Assert.Equal(6, await m.Get());
        await v.Set(2); // same value: observers re-verify (CacheCheck), they do not recompute
        Assert.Equal(6, await m.Get());
        Assert.Equal(1, computeCount);
    }

    [Fact(Timeout = 10000)]
    public async Task Diamond_RecomputesEachNodeOnce_PerInvalidation()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(1);
        int c1 = 0, c2 = 0, c3 = 0;
        var m1 = f.CreateActorMemoizR(async () => { Interlocked.Increment(ref c1); return await v.Get(); });
        var m2 = f.CreateActorMemoizR(async () => { Interlocked.Increment(ref c2); return await v.Get() * 2; });
        var m3 = f.CreateActorMemoizR(async () => { Interlocked.Increment(ref c3); return await m1.Get() + await m2.Get(); });

        Assert.Equal(3, await m3.Get());
        await v.Set(2);
        Assert.Equal(6, await m3.Get());

        // One recompute per node per invalidation: m1's changed value diamond-marks m3 while m3
        // is consuming it -- the mark is absorbed (no generation bump), not recomputed again.
        Assert.Equal(2, c1);
        Assert.Equal(2, c2);
        Assert.Equal(2, c3);
    }

    [Fact(Timeout = 10000)]
    public async Task DynamicRewiring_DropsAndAcquiresSources()
    {
        var f = new MemoFactory();
        var useX = f.CreateActorSignal(true);
        var x = f.CreateActorSignal(10);
        var y = f.CreateActorSignal(20);
        var computeCount = 0;
        var m = f.CreateActorMemoizR(async () =>
        {
            Interlocked.Increment(ref computeCount);
            return await useX.Get() ? await x.Get() : await y.Get();
        });

        Assert.Equal(10, await m.Get());

        await y.Set(21); // not a source yet: must not dirty m
        Assert.Equal(10, await m.Get());
        Assert.Equal(1, computeCount);

        await useX.Set(false); // branch switch: m now reads y
        Assert.Equal(21, await m.Get());
        Assert.Equal(2, computeCount);

        await x.Set(11); // no longer a source: must not dirty m
        Assert.Equal(21, await m.Get());
        Assert.Equal(2, computeCount);

        await y.Set(22); // current source: must dirty m
        Assert.Equal(22, await m.Get());
        Assert.Equal(3, computeCount);
    }

    // I2 ported: a Set landing while a recompute is in flight bumps the generation, so the
    // recompute's commit is refused and the next Get recomputes -- the memo can never cache a
    // value the invalidation predates.
    [Fact(Timeout = 10000)]
    public async Task StaleDuringRecompute_IsNotClobbered()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(1);
        var armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var m = f.CreateActorMemoizR(async () =>
        {
            var value = await v.Get();
            if (Volatile.Read(ref armed))
            {
                entered.TrySetResult(); // the retry after the refused commit passes here again
                await gate.Task;
            }

            return value;
        });

        Assert.Equal(1, await m.Get()); // prime: wires the v -> m observer link

        await v.Set(2);
        Volatile.Write(ref armed, true);

        var parked = m.Get(); // recompute reads v == 2... and parks
        await entered.Task;

        await v.Set(3); // lands mid-recompute: generation bump dooms the in-flight commit
        gate.SetResult();

        Assert.Equal(2, await parked); // the parked Get returns what it computed...
        Assert.Equal(3, await m.Get()); // ...but nothing cached it as Clean: the next Get recomputes
    }

    // I3 ported: a Stale that does NOT escalate the state (the node is already CacheCheck) must
    // still bump the generation -- otherwise a node parked in its parent scan commits Clean over
    // the pending dirty parent and, because cascades stop at already-dirty nodes, stays stale
    // forever. The final convergence assertion is the detector.
    [Fact(Timeout = 10000)]
    public async Task SuppressedStaleDuringParentCheck_IsNotClobbered()
    {
        var f = new MemoFactory();
        var s = f.CreateActorSignal(2);
        var armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // p = s / 2 with adjacent s values chosen so p's value does NOT change: the diamond
        // down-link cannot mask the suppressed-stale hole.
        var p = f.CreateActorMemoizR(async () =>
        {
            var value = await s.Get();
            if (Volatile.Read(ref armed))
            {
                entered.TrySetResult(); // the retry after the refused commit passes here again
                await gate.Task;
            }

            return value / 2;
        });
        var c = f.CreateActorMemoizR(async () => await p.Get() * 10);

        Assert.Equal(10, await c.Get()); // prime: s=2, p=1, c=10

        await s.Set(3); // p -> Dirty, c -> Check; p's value will stay 1 (3/2)
        Volatile.Write(ref armed, true);

        var parked = c.Get(); // c's parent scan recomputes p, which parks mid-fn (s already read as 3)
        await entered.Task;

        await s.Set(5); // p Dirty again; c's Stale(Check) is SUPPRESSED (already Check) -- but must bump c's generation
        gate.SetResult();

        Assert.Equal(10, await parked); // p recomputed to an unchanged 1; c's commit was refused, value still old

        // The detector: were the suppressed bump missing, c would have committed Clean(10) over
        // p's pending Dirty and no later cascade could ever re-dirty it.
        Assert.Equal(20, await c.Get()); // p = 5/2 = 2, c = 20
    }

    [Fact(Timeout = 10000)]
    public async Task ConcurrentGetsOfOneDirtyMemo_ComputeOnce()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(7);
        var computeCount = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var m = f.CreateActorMemoizR(async () =>
        {
            Interlocked.Increment(ref computeCount);
            entered.TrySetResult();
            await gate.Task;
            return await v.Get();
        });

        var first = m.Get();
        await entered.Task;

        // Arrivals during the evaluation park on waiter tasks and re-decide once it commits.
        var others = Enumerable.Range(0, 5).Select(_ => m.Get()).ToArray();
        gate.SetResult();

        Assert.Equal(7, await first);
        foreach (var other in others)
        {
            Assert.Equal(7, await other);
        }

        Assert.Equal(1, computeCount);
    }

    [Fact(Timeout = 10000)]
    public async Task IndependentMemos_ComputeInParallel()
    {
        var f = new MemoFactory();
        var entered1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var m1 = f.CreateActorMemoizR(async () => { entered1.SetResult(); await gate.Task; return 1; });
        var m2 = f.CreateActorMemoizR(async () => { entered2.SetResult(); await gate.Task; return 2; });

        var g1 = m1.Get();
        var g2 = m2.Get();

        // Both computations are in flight at once: the actor serializes bookkeeping turns,
        // never user computations -- there is no global evaluation lock to collapse this.
        await entered1.Task;
        await entered2.Task;
        gate.SetResult();

        Assert.Equal(1, await g1);
        Assert.Equal(2, await g2);
    }

    [Fact(Timeout = 10000)]
    public async Task Cycle_IsDetected_ByFlowIdentity()
    {
        var f = new MemoFactory();
        ActorMemo<int> a = null!;
        var b = f.CreateActorMemoizR(async () => await a.Get());
        a = f.CreateActorMemoizR(async () => await b.Get());

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Get());
    }

    [Fact(Timeout = 10000)]
    public async Task SetInsideComputation_Throws_AndTheMemoRetries()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(1);
        var misbehave = true;
        var m = f.CreateActorMemoizR<int>(async () =>
        {
            if (Volatile.Read(ref misbehave))
            {
                await v.Set(99); // feedback loop: rejected on the flow, like the lock engine's exclusive-inside-upgradeable
            }

            return await v.Get();
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => m.Get());
        Assert.Contains("inside a reactive computation", ex.Message);

        // The failed computation parks the memo Dirty, so the next Get retries.
        Volatile.Write(ref misbehave, false);
        Assert.Equal(1, await m.Get());
    }

    [Fact(Timeout = 10000)]
    public async Task ThrowingComputation_FaultsTheGet_AndIsRetriedOnTheNextGet()
    {
        var f = new MemoFactory();
        var fail = true;
        var m = f.CreateActorMemoizR(async () =>
        {
            await Task.Yield();
            return Volatile.Read(ref fail) ? throw new InvalidDataException("boom") : 42;
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => m.Get());

        Volatile.Write(ref fail, false);
        Assert.Equal(42, await m.Get()); // Dirty-on-throw: the first-run failure does not strand the memo
    }

    // A CacheCheck node whose parent scan hits a now-throwing parent must NOT commit Clean over
    // that unverified parent: the parent stays Dirty, and a later write to an already-dirty node
    // suppresses its cascade, so the node would serve its last good value forever. It must stay
    // CacheCheck and retry the parent on the next Get (the actor twin of the lock engine's
    // MemoBase parent-faulted handling).
    [Fact(Timeout = 10000)]
    public async Task ScanWithFaultingParent_DoesNotCommitCleanOverIt_AndRetriesOnRecovery()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(1);
        var fail = false;
        var p = f.CreateActorMemoizR(async () =>
        {
            var x = await v.Get();
            return Volatile.Read(ref fail) ? throw new InvalidOperationException("p boom") : x;
        });
        var c = f.CreateActorMemoizR(async () => await p.Get() + 100);

        Assert.Equal(101, await c.Get()); // prime: v=1, p=1, c=101 (all clean)

        Volatile.Write(ref fail, true);
        await v.Set(2); // p -> Dirty, c -> CacheCheck

        // c scans p; p recomputes and throws. The fault is best-effort swallowed (Get serves the
        // last good value), but c must not latch Clean over the still-dirty parent.
        Assert.Equal(101, await c.Get());

        Volatile.Write(ref fail, false); // p can recompute again

        // With the bug, c is Clean and serves 101 forever; with the fix, c stayed CacheCheck,
        // re-scans the recovered p (now 2) and reflects it.
        Assert.Equal(102, await c.Get());
    }

    [Fact(Timeout = 20000)]
    public async Task ConcurrentSetsAndGets_StayConsistent_AndConverge()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(0);
        var m1 = f.CreateActorMemoizR(async () => await v.Get() * 2);
        var m2 = f.CreateActorMemoizR(async () => await m1.Get() + 1);

        const int writes = 200;
        var writer = Task.Run(async () =>
        {
            for (var i = 1; i <= writes; i++)
            {
                await v.Set(i);
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!writer.IsCompleted)
            {
                var value = await m2.Get();
                // m2 = v*2+1 for SOME v: any even read would be a torn/inconsistent commit.
                Assert.True(value % 2 == 1, $"observed inconsistent value {value}");
            }
        })).ToArray();

        await writer;
        await Task.WhenAll(readers);

        // Convergence: once writes stop, the pull path must settle on the final value.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (await m2.Get() != writes * 2 + 1 && sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
        }

        Assert.Equal(writes * 2 + 1, await m2.Get());
    }

    // The late-wiring guard's regression test (the lock engine FAILS this very scenario --
    // quarantined as RegressionTests.LockEngine_UnprimedChainUnderStorm_StrandsStale_KnownIssue):
    // the chain is deliberately NOT primed, so the observer links wire mid-storm, after the
    // sources are already dirty -- the window where cascade suppression silences a late-wired
    // observer permanently. The read-evidence pairs ((source, generation) captured in the turn
    // that served each value, re-verified at commit) park such commits Dirty instead of letting
    // them cache a value that predates an unseen invalidation.
    [Fact(Timeout = 120000)]
    public async Task UnprimedChainUnderStorm_NeverStrandsStale()
    {
        for (var round = 0; round < 20; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(RunUnprimedChainStormInstanceAsync)));
        }
    }

    private static async Task RunUnprimedChainStormInstanceAsync()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(0);
        var m1 = f.CreateActorMemoizR(async () => await v.Get() * 2);
        var m2 = f.CreateActorMemoizR(async () => await m1.Get() + 1);

        const int writes = 200;
        var writer = Task.Run(async () =>
        {
            for (var i = 1; i <= writes; i++)
            {
                await v.Set(i);
            }
        });
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (!writer.IsCompleted)
            {
                var value = await m2.Get();
                Assert.True(value % 2 == 1, $"inconsistent value {value}");
            }
        })).ToArray();

        await writer;
        await Task.WhenAll(readers);

        for (var k = 0; k < 300 && await m2.Get() != writes * 2 + 1; k++)
        {
            await Task.Delay(2);
        }

        Assert.Equal(writes * 2 + 1, await m2.Get());
    }

    [Fact]
    public void StrictFactory_AppliesSendableChecks_ToActorNodes()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks);
        Assert.Throws<InvalidOperationException>(() => f.CreateActorSignal(new List<int>()));
        Assert.Throws<InvalidOperationException>(() => f.CreateActorMemoizR(async () => new List<int>()));
        Assert.NotNull(f.CreateActorSignal(1));
    }

    // The commit turn runs user code (the value comparison's Equals). A throw there must NOT
    // strand the node in Evaluating with its waiters never released -- the failure mode the
    // try/finally in Commit closes. A second Get parked as a waiter on the throwing evaluation
    // must be released (and itself fault on retry), not hang forever.
    [Fact(Timeout = 10000)]
    public async Task ThrowingEqualsInCommit_ReleasesWaiters_AndDoesNotWedge()
    {
        var f = new MemoFactory();
        var v = f.CreateActorSignal(0);
        var armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var m = f.CreateActorMemoizR(async () =>
        {
            if (Volatile.Read(ref armed))
            {
                entered.TrySetResult();
                await gate.Task;
            }

            return new ThrowsOnEquals(await v.Get());
        });

        await m.Get(); // prime: oldValue is null, so Equals is not yet called
        await v.Set(1); // dirty
        Volatile.Write(ref armed, true);

        var evaluating = m.Get(); // claims the evaluation, parks mid-fn
        await entered.Task;
        var waiter = m.Get(); // parks as a waiter on the in-flight evaluation
        gate.SetResult(); // the evaluating Get resumes; its Commit calls Equals(old, new) -> throws

        // The evaluating Get faults (with the fix, EndEvaluation still runs in the finally)...
        await Assert.ThrowsAsync<InvalidOperationException>(() => evaluating);
        // ...and the waiter was released rather than hung; on retry it recomputes and faults too.
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiter);

        // The node is not wedged: a fresh Get still reaches the computation (and faults again).
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Get());
    }

    private sealed class ThrowsOnEquals(int seed)
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException("equals boom");

        public override int GetHashCode() => seed;
    }
}

// The GraphActor's own contracts: turns are serialized (plain shared state inside turns needs
// no synchronization), exceptions propagate to the turn's awaiter without killing the loop,
// nested Run calls execute inline as part of the current turn, and the actor doubles as a
// layer-4 IExecutor with exact IsCurrent identity.
public class GraphActorTests
{
    [Fact(Timeout = 10000)]
    public async Task Turns_AreSerialized_PlainStateNeedsNoLocks()
    {
        var actor = new GraphActor();
        var counter = 0; // deliberately unsynchronized: the actor IS the synchronization

        var callers = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                await actor.Run(() => counter++);
            }
        }));

        await Task.WhenAll(callers);
        Assert.Equal(10_000, await actor.Run(() => counter));
    }

    [Fact(Timeout = 10000)]
    public async Task ThrowingTurn_FaultsItsCaller_AndTheLoopSurvives()
    {
        var actor = new GraphActor();
        await Assert.ThrowsAsync<InvalidDataException>(() => actor.Run(() => throw new InvalidDataException("turn")));
        Assert.Equal(7, await actor.Run(() => 7)); // the next turn runs normally
    }

    [Fact(Timeout = 10000)]
    public async Task IsCurrent_IsExact_AndNestedRunExecutesInline()
    {
        var actor = new GraphActor();
        Assert.False(actor.IsCurrent);
        Assert.Throws<InvalidOperationException>(() => actor.AssertIsolated());

        var (isCurrentInTurn, nestedRanInline) = await actor.Run(() =>
        {
            // A Run from within a turn must join the current turn (queueing would deadlock).
            var ranInline = false;
            _ = actor.Run(() => ranInline = true);
            return (actor.IsCurrent, ranInline);
        });

        Assert.True(isCurrentInTurn);
        Assert.True(nestedRanInline);
        Assert.False(actor.IsCurrent);
    }

    [Fact(Timeout = 10000)]
    public async Task GraphActor_ServesAsAnIExecutor()
    {
        IExecutor executor = new GraphActor();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.Enqueue(() => tcs.SetResult(executor.IsCurrent));
        Assert.True(await tcs.Task);
    }
}
