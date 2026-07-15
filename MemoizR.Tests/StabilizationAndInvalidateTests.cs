using MemoizR.Reactive;

namespace MemoizR.Tests;

// Phase 1 of ADR 0007: MemoBase.Invalidate() (the refresh primitive) and the stabilization
// notification (the internal "committed Clean" half of the observer protocol). These pin the
// contracts transitions and pending indicators are built on.
public class StabilizationAndInvalidateTests
{
    // Records every stabilization callback; ConcurrentQueue because commits arrive from
    // reaction update flows, not the test flow.
    private sealed class RecordingListener : IStabilizationListener
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<int> Tokens = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<Exception> Faults = new();

        public void OnStabilized(SignalHandlR node, int token) => Tokens.Enqueue(token);

        public void OnStabilizationFaulted(SignalHandlR node, int token, Exception exception) => Faults.Enqueue(exception);
    }

    [Fact]
    public async Task Invalidate_ForcesRecomputeOnNextGet()
    {
        var f = new MemoFactory();
        var external = 0;
        var invocations = 0;
        var m = f.CreateMemoizR(() =>
        {
            Interlocked.Increment(ref invocations);
            return Task.FromResult(Volatile.Read(ref external));
        });

        Assert.Equal(0, await m.Get());
        Assert.Equal(0, await m.Get());
        Assert.Equal(1, invocations);

        // Out-of-band state changed; the memo cannot know until it is invalidated.
        Volatile.Write(ref external, 5);
        Assert.Equal(0, await m.Get());
        Assert.Equal(1, invocations);

        await m.Invalidate();
        Assert.Equal(5, await m.Get());
        Assert.Equal(2, invocations);

        // Memoized again after the refresh.
        Assert.Equal(5, await m.Get());
        Assert.Equal(2, invocations);
    }

    [Fact(Timeout = 5000)]
    public async Task Invalidate_ReactionReRunsOnlyWhenTheValueActuallyChanged()
    {
        var f = new MemoFactory();
        var external = 0;
        var m = f.CreateMemoizR(() => Task.FromResult(Volatile.Read(ref external)));
        var executions = 0;
        var r = f.BuildReaction().CreateReaction(m, v => Interlocked.Increment(ref executions));

        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref executions) == 1);

        // The reaction's stabilization notification doubles as the deterministic "this update
        // pass finished" probe -- it fires on the CacheCheck resolution even when the action
        // does not run.
        var listener = new RecordingListener();
        r.AddStabilizationListener(listener);
        var tokensBefore = listener.Tokens.Count;

        // Refresh with an UNCHANGED value: the memo recomputes, the reaction re-checks and
        // commits without executing its side effect.
        await m.Invalidate();
        await TestHelpers.WaitForConvergenceAsync(() => listener.Tokens.Count > tokensBefore);
        Assert.Equal(1, Volatile.Read(ref executions));

        // Refresh with a CHANGED value: now the side effect re-runs.
        Volatile.Write(ref external, 7);
        await m.Invalidate();
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref executions) == 2);
        Assert.Equal(2, Volatile.Read(ref executions));
    }

    [Fact]
    public async Task Invalidate_InsideComputation_IsRejectedLikeSet()
    {
        var f = new MemoFactory();
        var m1 = f.CreateMemoizR(() => Task.FromResult(1));
        Assert.Equal(1, await m1.Get());

        var m2 = f.CreateMemoizR(async () =>
        {
            await m1.Invalidate();
            return 2;
        });

        // Same contract as Signal.Set inside a computation (MZR003): the exclusive acquisition
        // inside the evaluation's upgradeable lock is rejected instead of deadlocking.
        await Assert.ThrowsAsync<InvalidOperationException>(() => m2.Get());
    }

    [Fact(Timeout = 5000)]
    public async Task Invalidate_MidEvaluation_RefusesTheStaleCommit()
    {
        var f = new MemoFactory();
        var gate = new RecomputeGate();
        var external = 0;
        var invocations = 0;
        var m = f.CreateMemoizR(async () =>
        {
            var value = Volatile.Read(ref external);
            Interlocked.Increment(ref invocations);
            await gate.PauseIfArmedAsync();
            return value;
        });

        Assert.Equal(0, await m.Get());
        var listener = new RecordingListener();
        m.AddStabilizationListener(listener);

        Volatile.Write(ref external, 1);
        await m.Invalidate();

        gate.Arm();
        var pending = Task.Run(() => m.Get());
        await gate.ReadDone; // the recompute is parked, having already read external == 1

        // A refresh landing mid-evaluation must refuse that evaluation's commit: without the
        // generation bump the node would cache 1 as Clean and never observe external == 2.
        Volatile.Write(ref external, 2);
        await m.Invalidate();

        gate.Proceed();
        Assert.Equal(1, await pending);   // the evaluation returns what it computed...
        Assert.Empty(listener.Tokens);    // ...but no stabilization was notified for it
        Assert.Equal(2, await m.Get());   // and the next Get recomputes fresh
        Assert.Equal(3, invocations);
        var token = Assert.Single(listener.Tokens);
        Assert.Equal(m.stateCell.LastCleanCommitToken, token);
    }

    [Fact]
    public async Task StabilizationListener_NotifiedOnMemoCommit_AndLazyUntilPulled()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await v.Get() * 2);
        Assert.Equal(2, await m.Get());

        var listener = new RecordingListener();
        m.AddStabilizationListener(listener);

        // Pull-based laziness (ADR 0007): a Set only marks the memo stale; nothing stabilizes
        // -- so nothing is notified -- until someone pulls.
        await v.Set(5);
        Assert.Empty(listener.Tokens);

        Assert.Equal(10, await m.Get());
        var token = Assert.Single(listener.Tokens);
        Assert.Equal(m.stateCell.LastCleanCommitToken, token);

        // A removed listener hears nothing further.
        m.RemoveStabilizationListener(listener);
        await v.Set(6);
        Assert.Equal(12, await m.Get());
        Assert.Single(listener.Tokens);
    }

    [Fact]
    public async Task StabilizationListener_NotifiedWhenCacheCheckResolvesWithoutRecompute()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var m1 = f.CreateMemoizR(async () => await v.Get() > 0 ? 1 : 0);
        var m2Invocations = 0;
        var m2 = f.CreateMemoizR(async () =>
        {
            Interlocked.Increment(ref m2Invocations);
            return await m1.Get() * 10;
        });

        Assert.Equal(10, await m2.Get());
        var listener = new RecordingListener();
        m2.AddStabilizationListener(listener);

        // v changes, but m1 recomputes to the same value: m2's CacheCheck resolves without a
        // recompute -- and that commit must still notify (a transition anchored on m2 would
        // otherwise never complete for this write).
        await v.Set(2);
        Assert.Equal(10, await m2.Get());
        Assert.Single(listener.Tokens);
        Assert.Equal(1, m2Invocations);
    }

    [Fact(Timeout = 5000)]
    public async Task StabilizationListener_NotifiedOnReactionCommit()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var observed = 0;
        var r = f.BuildReaction().CreateReaction(v, x => Volatile.Write(ref observed, x));

        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref observed) == 1);

        var listener = new RecordingListener();
        r.AddStabilizationListener(listener);
        var tokensBefore = listener.Tokens.Count;

        await v.Set(5);
        await TestHelpers.WaitForConvergenceAsync(
            () => Volatile.Read(ref observed) == 5 && listener.Tokens.Count > tokensBefore);

        Assert.Equal(5, Volatile.Read(ref observed));
        Assert.True(listener.Tokens.Count > tokensBefore);
    }
}
