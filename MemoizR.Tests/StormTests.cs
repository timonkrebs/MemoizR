namespace MemoizR.Tests;

// The storm tests hammer the thread pool with parallel writers/readers for seconds at a time.
// Run in parallel with the rest of the suite they starve the pool and blow the tight
// [Fact(Timeout = ...)] budgets of the ordinary tests on 2-4 core CI runners (observed as
// spurious timeouts in unrelated legs), so this collection takes them OUT of the parallel
// schedule: xunit runs a DisableParallelization collection sequentially, alongside nothing.
[CollectionDefinition("SequentialStorms", DisableParallelization = true)]
public class SequentialStormsCollection
{
}

[Collection("SequentialStorms")]
public class ActorEngineStormTests
{
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
        }, TestContext.Current.CancellationToken);

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
            await Task.Delay(10, TestContext.Current.CancellationToken);
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

    // The reverse mixing direction: a lock-engine computation reading an actor node registers a
    // dependency in NEITHER graph (lock computations carry no ActorFlow frame; actor nodes
    // implement no lock-engine interface), so it would cache a value no later actor Set can
    // invalidate. Rejected at the read; top-level reads stay legal (untracked by definition).
    [Fact(Timeout = 10000)]
    public async Task ActorRead_InsideALockEngineComputation_IsRejected()
    {
        var f = new MemoFactory();
        var actorValue = f.CreateActorSignal(1);
        var actorMemo = f.CreateActorMemoizR(async () => await actorValue.Get() + 1);

        var readsActorSignal = f.CreateMemoizR(async () => await actorValue.Get());
        var readsActorMemo = f.CreateMemoizR(async () => await actorMemo.Get());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => readsActorSignal.Get());
        Assert.Contains("lock-engine computation", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => readsActorMemo.Get());

        // Outside any computation the same reads are fine -- and the actor graph still works.
        Assert.Equal(1, await actorValue.Get());
        Assert.Equal(2, await actorMemo.Get());
    }

    // The final mixing direction: a LOCK-ENGINE node read from inside an actor computation
    // registers in neither graph (the lock getter sees no capturing CurrentReaction on the
    // actor fn's flow; actor frames cannot capture lock sources), so the actor memo would cache
    // a value no lock-side Set can ever invalidate. Rejected at the lock engine's read entry
    // points, gated on the actor engine being in use at all.
    [Fact(Timeout = 10000)]
    public async Task LockRead_InsideAnActorComputation_IsRejected()
    {
        var f = new MemoFactory();
        var lockSignal = f.CreateSignal(1);
        var lockMemo = f.CreateMemoizR(async () => await lockSignal.Get());

        var readsLockSignal = f.CreateActorMemoizR(async () => await lockSignal.Get());
        var readsLockMemo = f.CreateActorMemoizR(async () => await lockMemo.Get());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => readsLockSignal.Get());
        Assert.Contains("actor computation", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => readsLockMemo.Get());

        // Outside actor computations the same lock nodes work as ever.
        Assert.Equal(1, await lockMemo.Get());
    }

    // The same staleness with the computation and the actor node in DIFFERENT contexts: the
    // guard must detect a capturing computation of ANY context on the flow (the flow-ambient
    // LockEngineFlow marker), not just the actor node's own.
    [Fact(Timeout = 10000)]
    public async Task ActorRead_InsideALockComputationOfAnotherContext_IsRejected()
    {
        var lockFactory = new MemoFactory();
        var actorFactory = new MemoFactory();
        var actorValue = actorFactory.CreateActorSignal(1);

        var mixed = lockFactory.CreateMemoizR(async () => await actorValue.Get());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mixed.Get());
        Assert.Contains("lock-engine computation", ex.Message);
    }

    // A tracked read of a node from another context would capture a foreign source, and the
    // commit turn would then mutate that source's observer list on the WRONG actor. Rejected at
    // the read, on the flow, with an error naming the mistake.
    [Fact(Timeout = 10000)]
    public async Task CrossContextActorRead_IsRejected()
    {
        var fa = new MemoFactory();
        var fb = new MemoFactory();
        var foreignSignal = fb.CreateActorSignal(1);
        var foreignMemo = fb.CreateActorMemoizR(async () => await foreignSignal.Get() + 1);

        var readsForeignSignal = fa.CreateActorMemoizR(async () => await foreignSignal.Get());
        var readsForeignMemo = fa.CreateActorMemoizR(async () => await foreignMemo.Get());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => readsForeignSignal.Get());
        Assert.Contains("different context", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => readsForeignMemo.Get());

        // Untracked top-level reads across factories stay legal -- only DEPENDING is confined.
        Assert.Equal(1, await foreignSignal.Get());
    }
}

[Collection("SequentialStorms")]
public class LockEngineStormTests
{
    // The lock-engine twin of UnprimedChainUnderStorm_NeverStrandsStale above, and the
    // regression guard for the late-wiring hole this whole effort surfaced (ADR 0006): an observer
    // that wired itself to an ALREADY-DIRTY source only at the END of its evaluation was never
    // reached by the cascade that dirtied the source, and -- because cascades terminate at an
    // already-dirty node ("observers were already notified") -- never would be: it committed Clean
    // over a dirty parent and stayed permanently stale. Every other stress test primes its graph
    // before storming, which is why this stayed hidden; this one is deliberately UN-PRIMED, so the
    // links wire during the first evaluations, mid-storm. The lock engine closes the hole by
    // subscribing EAGERLY at capture time (Context.CheckDependenciesTheSame), so an in-flight Set
    // reaches the node and bumps its generation before it can commit -- the same outcome the actor
    // engine reaches via read-evidence pairs. Reverting that eager subscription fails this within
    // a round or two (verified), so it is a genuine guard, not a tautology.
    [Fact(Timeout = 300000)]
    public async Task LockEngine_UnprimedChainUnderStorm_NeverStrandsStale()
    {
        for (var round = 0; round < 20; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(RunUnprimedLockChainStormInstanceAsync)));
        }
    }

    private static async Task RunUnprimedLockChainStormInstanceAsync()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(0);
        var m1 = f.CreateMemoizR(async () => await v.Get() * 2);
        var m2 = f.CreateMemoizR(async () => await m1.Get() + 1);

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
}
