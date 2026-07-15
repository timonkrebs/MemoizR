using MemoizR.Reactive;

namespace MemoizR.Tests;

// Phase 2 of ADR 0007: transition scopes (BeginTransition / Settled / Pending) and the
// per-reaction IsPending flag. The gated AdvancedReaction bodies make the pending windows
// deterministic: an effect parked on a TaskCompletionSource cannot commit, so IsPending is
// provably true at the assertion point.
public class TransitionTests
{
    [Fact(Timeout = 5000)]
    public async Task Transition_SettlesAfterTheReachedReactionCommits()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var observed = 0;
        var r = f.BuildReaction().CreateReaction(v, x => Volatile.Write(ref observed, x));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref observed) == 1);

        var t = f.BeginTransition();
        await v.Set(5);
        t.Dispose();

        await t.Settled;
        Assert.False(t.IsPending);
        Assert.Equal(5, Volatile.Read(ref observed));
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_AwaitUsing_IsTheOnSettledAnalog()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var observed = 0;
        var r = f.BuildReaction().CreateReaction(v, x => Volatile.Write(ref observed, x));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref observed) == 1);

        await using (var t = f.BeginTransition())
        {
            await v.Set(7);
        }

        // DisposeAsync sealed the scope and awaited settlement: the effect has applied.
        Assert.Equal(7, Volatile.Read(ref observed));
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_IsPendingWhileTheEffectIsInFlight()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            var x = await v.Get();
            if (x == 5)
            {
                await gate.Task;
            }
        });
        await TestHelpers.WaitForConvergenceAsync(() => !r.IsPendingSnapshot);

        var t = f.BeginTransition();
        await v.Set(5);
        t.Dispose();

        // The effect is parked on the gate, so it cannot have committed: both the transition
        // and the reaction are deterministically pending here.
        Assert.True(t.IsPending);
        Assert.True(await t.Pending.Get());
        Assert.True(r.IsPendingSnapshot);
        Assert.True(await r.IsPending.Get());

        gate.SetResult();
        await t.Settled;
        Assert.False(t.IsPending);

        // The reactive projections are published from a detached pump; they converge.
        await TestHelpers.WaitForConvergenceAsync(() => t.Pending.Get().Result == false);
        await TestHelpers.WaitForConvergenceAsync(() => r.IsPending.Get().Result == false);
        Assert.False(r.IsPendingSnapshot);
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_MemoOnlyWavefront_SettlesAtDispose()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await v.Get() * 2);
        Assert.Equal(2, await m.Get());

        var t = f.BeginTransition();
        await v.Set(5);

        // Pull-based laziness: no reaction was reached, so nothing is in flight (the memo
        // recomputes whenever someone pulls) -- the transition settles at the seal.
        Assert.False(t.IsPending);
        t.Dispose();
        await t.Settled;
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_DisposedWithoutWrites_SettlesImmediately()
    {
        var f = new MemoFactory();
        var t = f.BeginTransition();
        t.Dispose();
        await t.Settled;
        Assert.False(t.IsPending);
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_TracksEveryReachedReaction()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r1 = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            if (await v.Get() == 5) await gate1.Task;
        });
        var r2 = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            if (await v.Get() == 5) await gate2.Task;
        });
        await TestHelpers.WaitForConvergenceAsync(() => !r1.IsPendingSnapshot && !r2.IsPendingSnapshot);

        var t = f.BeginTransition();
        await v.Set(5);
        t.Dispose();

        Assert.True(t.IsPending);
        gate1.SetResult();
        await TestHelpers.WaitForConvergenceAsync(() => !r1.IsPendingSnapshot);
        Assert.True(t.IsPending); // one of two reached reactions is still in flight

        gate2.SetResult();
        await t.Settled;
        Assert.False(t.IsPending);
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_ReachesReactionsThroughDerivedMemos()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await v.Get() * 2);
        var observed = 0;
        var r = f.BuildReaction().CreateReaction(m, x => Volatile.Write(ref observed, x));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref observed) == 2);

        // The invalidation cascade runs synchronously on the tagged flow, so the transition
        // sees the whole transitive wavefront, not just the signal's direct observers: the
        // settlement below can only be correct if the reaction registered through the memo.
        await using (var t = f.BeginTransition())
        {
            await v.Set(5);
        }

        Assert.Equal(10, Volatile.Read(ref observed));
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_FaultedEffect_FaultsSettled()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var r = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            if (await v.Get() == 5)
            {
                throw new InvalidOperationException("boom");
            }
        });
        await TestHelpers.WaitForConvergenceAsync(() => !r.IsPendingSnapshot);

        var t = f.BeginTransition();
        await v.Set(5);
        t.Dispose();

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => t.Settled);
        var inner = Assert.Single(aggregate.InnerExceptions);
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(inner).Message);
        Assert.False(t.IsPending);
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_DisposedReaction_ReleasesTheTransition()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            if (await v.Get() == 5) await gate.Task;
        });
        await TestHelpers.WaitForConvergenceAsync(() => !r.IsPendingSnapshot);

        var t = f.BeginTransition();
        await v.Set(5);
        t.Dispose();
        Assert.True(t.IsPending);

        // A dead reaction can never commit; the transition must not stay pending forever.
        r.Dispose();
        await t.Settled;
        Assert.False(t.IsPending);
        gate.SetResult(); // unpark the abandoned effect
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_PausedReaction_KeepsPendingUntilResumeCommits()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var observed = 0;
        var r = f.BuildReaction().CreateReaction(v, x => Volatile.Write(ref observed, x));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref observed) == 1);

        r.Pause();
        var t = f.BeginTransition();
        await v.Set(5);
        t.Dispose();

        // The paused update ran and parked dirty without committing: the reaction itself is
        // not pending (nothing scheduled) but the transition honestly still is.
        await TestHelpers.WaitForConvergenceAsync(() => !r.IsPendingSnapshot);
        Assert.True(t.IsPending);
        Assert.NotEqual(5, Volatile.Read(ref observed));

        await r.Resume();
        await t.Settled;
        Assert.Equal(5, Volatile.Read(ref observed));
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_PendingSignal_DrivesOtherReactions()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            if (await v.Get() == 5) await gate.Task;
        });
        await TestHelpers.WaitForConvergenceAsync(() => !r.IsPendingSnapshot);

        var t = f.BeginTransition();

        // A spinner is just another reaction -- on the transition's Pending node. Built OUTSIDE
        // the tagged flow's writes so it observes, not participates.
        var spinnerShown = false;
        var spinner = f.BuildReaction().CreateReaction(t.Pending, pending => Volatile.Write(ref spinnerShown, pending));

        await v.Set(5);
        t.Dispose();
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref spinnerShown));

        gate.SetResult();
        await t.Settled;
        await TestHelpers.WaitForConvergenceAsync(() => !Volatile.Read(ref spinnerShown));
    }
}
