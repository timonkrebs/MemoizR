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
    public async Task Reaction_IsPending_CoversResumeUpdates()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            if (await v.Get() == 5)
            {
                await gate.Task;
            }
        });
        await TestHelpers.WaitForConvergenceAsync(() => !r.IsPendingSnapshot);

        // Paused: the scheduled update parks out without executing, draining the counter.
        r.Pause();
        await v.Set(5);
        await TestHelpers.WaitForConvergenceAsync(() => !r.IsPendingSnapshot);

        // The resume update runs the arbitrary-length effect; the pending flag must cover it.
        var resume = r.Resume();
        await TestHelpers.WaitForConvergenceAsync(() => r.IsPendingSnapshot);
        Assert.True(r.IsPendingSnapshot);

        gate.SetResult();
        await resume;
        Assert.False(r.IsPendingSnapshot);
        await TestHelpers.WaitForConvergenceAsync(() => !r.IsPending.Get().Result);
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
    public async Task Transition_ParentFaultDuringCheck_FaultsSettled()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var shouldThrow = false;
        var m = f.CreateMemoizR(async () =>
        {
            var x = await v.Get();
            if (Volatile.Read(ref shouldThrow))
            {
                throw new InvalidOperationException("parent boom");
            }
            return x;
        });
        var observed = 0;
        var r = f.BuildReaction().CreateReaction(m, x => Volatile.Write(ref observed, x));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref observed) == 1);

        // The write reaches the reaction only as CacheCheck; the parent scan then hits the
        // faulting memo, no commit follows, and nothing reschedules -- the transition must
        // hear the fault instead of hanging on a wavefront that already failed.
        Volatile.Write(ref shouldThrow, true);
        var t = f.BeginTransition();
        await v.Set(5);
        t.Dispose();

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => t.Settled);
        var inner = Assert.Single(aggregate.InnerExceptions);
        Assert.Equal("parent boom", Assert.IsType<InvalidOperationException>(inner).Message);
        Assert.False(t.IsPending);
        Assert.Equal(1, Volatile.Read(ref observed)); // the effect never ran with a broken parent
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_FaultThatBeatsRegistration_IsRecovered()
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

        // Fault once with NO transition listening: only the (token, exception) record remains.
        await v.Set(5);
        await TestHelpers.WaitForConvergenceAsync(() => r.LastStabilizationFault != null);

        // A registration whose threshold the recorded fault token satisfies must recover the
        // fault instead of waiting for a commit that will never come -- the fault mirror of the
        // LastCleanCommitToken recovery, driven directly through the internal registration.
        var t = f.BeginTransition();
        t.RegisterReached(r, r.LastStabilizationFault!.Token);
        t.Dispose();

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => t.Settled);
        Assert.Contains(aggregate.InnerExceptions, e => e.Message == "boom");
        Assert.False(t.IsPending);
    }

    [Fact(Timeout = 10000)]
    public async Task Transition_TracksWritesThroughAnAlreadyDirtyMemo()
    {
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await v.Get() * 2);
        var observed = 0;
        var r = f.BuildReaction()
            .AddTimeProvider(timeProvider)
            .AddDebounceTime(TimeSpan.FromMinutes(5))
            .CreateReaction(m, x => Volatile.Write(ref observed, x));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref observed) == 2);

        // Write 1 (untagged) dirties the memo; the frozen fake clock keeps the reaction's
        // update parked in its debounce window, so nothing pulls the memo clean.
        await v.Set(2);

        // The tagged write hits the ALREADY-DIRTY memo. The pruned cascade would hide it from
        // the reaction's registration -- the transition would settle at dispose with the effect
        // still unapplied; the wavefront-aware cascade must reach and register the reaction.
        var t = f.BeginTransition();
        await v.Set(5);
        t.Dispose();
        Assert.True(t.IsPending);

        timeProvider.Advance(TimeSpan.FromMinutes(6));
        await t.Settled;
        Assert.Equal(10, Volatile.Read(ref observed));
        Assert.False(t.IsPending);
    }

    [Fact(Timeout = 5000)]
    public async Task Transition_OldFault_DoesNotSettleANewerRegistration()
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

        await v.Set(5);
        await TestHelpers.WaitForConvergenceAsync(() => r.LastStabilizationFault != null);
        var oldFault = r.LastStabilizationFault!;

        // A registration with a NEWER threshold than the recorded fault: the old fault belongs
        // to a superseded trigger and must not fault this wavefront -- its own update is still
        // owed a commit-or-fault at or above the threshold.
        var t = f.BeginTransition();
        t.RegisterReached(r, oldFault.Token + 1);
        t.Dispose();
        Assert.True(t.IsPending);
        Assert.False(t.Settled.IsCompleted);

        // The newer trigger arrives and commits cleanly: the transition settles unfaulted.
        await v.Set(6);
        await t.Settled;
        Assert.False(t.IsPending);
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
