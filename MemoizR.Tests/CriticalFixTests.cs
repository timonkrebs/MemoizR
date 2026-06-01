namespace MemoizR.Tests;

// Regression tests for the two critical code-review findings:
//   C2 — a faulting upstream source must propagate, not leave a node CacheClean over a stale value.
//   C1 — the per-async-flow ReactionScope must have a stable identity and must not leak.
public class CriticalFixTests
{
    // ---- C2: faulting source must not be swallowed --------------------------

    [Fact]
    public async Task C2_FaultingSource_PropagatesThroughChild_AndDoesNotServeStaleClean()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var shouldThrow = false;

        var parent = f.CreateMemoizR(async () =>
        {
            var x = await v1.Get();
            if (shouldThrow) throw new InvalidOperationException("boom");
            return x;
        });
        var child = f.CreateMemoizR(async () => await parent.Get());

        // Prime: a clean value is cached through the whole chain.
        Assert.Equal(1, await child.Get());

        // Make the parent fault and mark the graph dirty.
        shouldThrow = true;
        await v1.Set(2);

        // C2: the fault must surface to the caller (previously it was swallowed,
        // leaving the child CacheClean over its stale value).
        await Assert.ThrowsAsync<InvalidOperationException>(child.Get);
        // And it must keep surfacing: the node must not settle to CacheClean over the stale value.
        await Assert.ThrowsAsync<InvalidOperationException>(child.Get);

        // Recovery: once the fault clears and inputs change, the chain recomputes cleanly.
        shouldThrow = false;
        await v1.Set(3);
        Assert.Equal(3, await child.Get());
    }

    [Fact]
    public async Task C2_FaultingMemo_DirectGet_KeepsThrowingUntilFixed()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var shouldThrow = false;

        var m = f.CreateMemoizR(async () =>
        {
            var x = await v1.Get();
            if (shouldThrow) throw new InvalidOperationException("boom");
            return x;
        });

        Assert.Equal(1, await m.Get());

        shouldThrow = true;
        await v1.Set(2);

        await Assert.ThrowsAsync<InvalidOperationException>(m.Get);
        await Assert.ThrowsAsync<InvalidOperationException>(m.Get);

        shouldThrow = false;
        await v1.Set(3);
        Assert.Equal(3, await m.Get());
    }

    // ---- C1: per-flow scope identity + no leak ------------------------------

    [Fact]
    public void C1_ReactionScope_SameAsyncFlow_ReturnsSameInstanceAndLock()
    {
        var f = new MemoFactory();
        var ctx = f.Context;

        // On a flow that never explicitly established a scope, the getter used to mint a fresh
        // ReactionScope (and a fresh ContextLock) on every access. It must now return the same
        // instance for the whole flow.
        var s1 = ctx.ReactionScope;
        var s2 = ctx.ReactionScope;

        Assert.Same(s1, s2);
        Assert.Same(s1.ContextLock, s2.ContextLock);
    }

    [Fact]
    public void C1_CleanScope_StartsAFreshScopeForTheFlow()
    {
        var f = new MemoFactory();
        var ctx = f.Context;

        var s1 = ctx.ReactionScope;
        ctx.CleanScope();
        var s2 = ctx.ReactionScope;

        Assert.NotSame(s1, s2);
    }

    [Fact(Timeout = 30000)]
    public async Task C1_HighIterationParallelSetGet_NoDeadlockNoCorruption()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(0);
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        const int taskCount = 32;
        const int iterations = 500;

        var work = Enumerable.Range(0, taskCount).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await v1.Set(i);
                _ = await m1.Get();
            }
        })).ToArray();

        // Must not deadlock, throw, or corrupt internal state under heavy concurrent Set/Get.
        await Task.WhenAll(work);

        // After the storm settles, a final Set is reflected by the memo.
        await v1.Set(21);
        Assert.Equal(42, await m1.Get());
    }
}
