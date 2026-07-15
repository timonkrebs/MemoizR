using MemoizR.Reactive;

namespace MemoizR.Distributed;

/// <summary>
/// The glitch barrier over mirrored inputs: renders only on a CONSISTENT, VERIFIED snapshot of
/// the host's write history, and drives recovery itself -- when the two mirrors' evidence
/// disagrees on a shared signal (or the mirrors sit on different incarnations of the host),
/// the lagging side is re-pulled on the bridge's own flow (a reaction body must never Set its
/// own graph's signals, so the re-pull is scheduled outside the evaluation) and the barrier
/// re-runs when the adoption lands. v0 combines mirrors of the SAME host graph; barriers
/// across different peers need the wire-v3 multi-peer epoch table.
/// </summary>
public static class DistributedBarrier
{
    /// <summary>
    /// A reaction over two mirrors that calls <paramref name="render"/> exactly on consistent
    /// verified snapshots. Keep the returned reaction (and the mirrors) strongly referenced.
    /// Re-pull faults are reported to <paramref name="onRepullError"/> (the next advertisement
    /// or heartbeat retries), and so is a throwing <paramref name="onGlitch"/> callback --
    /// diagnostics must never block the healing path; rendering is skipped until evidence is
    /// verified again.
    /// </summary>
    public static Reaction CreateConsistentReaction<T1, T2>(
        MemoFactory factory,
        RemoteSignal<T1> first,
        RemoteSignal<T2> second,
        Action<T1, T2> render,
        Action<Exception>? onRepullError = null,
        Action<CausalityStamp, CausalityStamp>? onGlitch = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(render);

        return factory.BuildReaction("distributed barrier").CreateReaction(
            first.Local, second.Local, (_, _) =>
            {
                // The graph parameters only TRIGGER the barrier. Rendering reads each mirror's
                // atomic publication instead: the parameter values were captured when the
                // propagation started, while an adoption landing mid-reaction swaps the side
                // evidence -- pairing the two would render an (old value, new stamp) snapshot
                // that never existed. Each publication binds value and evidence in one
                // reference, so the consistency check below vouches for exactly the values it
                // renders; a newer pair than the trigger saw is still a real published pair,
                // and its own propagation re-runs the barrier anyway.
                var firstPublication = first.Publication;
                var secondPublication = second.Publication;

                // Absent evidence (nothing synced yet) and unverifiable evidence mean the same
                // thing to a consumer: CANNOT VERIFY -- no render, never a guess. The pull that
                // heals an unverifiable spell is driven by advertisements/heartbeats, not here.
                if (firstPublication is null || secondPublication is null
                    || firstPublication.Unverifiable || secondPublication.Unverifiable)
                {
                    return;
                }

                // Incarnation identity first: stamps from different epochs never agree, but an
                // honestly-EMPTY stamp is epoch-agnostic and vacuously consistent with anything
                // -- without the header-epoch check, a restarted host's first empty publication
                // would render together with the other mirror's pre-restart state.
                if (firstPublication.Epoch != secondPublication.Epoch
                    || !firstPublication.Stamp.IsConsistentWith(secondPublication.Stamp))
                {
                    ReportGlitch(firstPublication.Stamp, secondPublication.Stamp, onGlitch, onRepullError);
                    RepullLagging(first, second, firstPublication, secondPublication, onRepullError);
                    return;
                }

                render(firstPublication.Value, secondPublication.Value);
            });
    }

    private static void ReportGlitch(
        CausalityStamp firstStamp,
        CausalityStamp secondStamp,
        Action<CausalityStamp, CausalityStamp>? onGlitch,
        Action<Exception>? onRepullError)
    {
        // Diagnostics only -- the barrier heals itself either way. A throwing sink (a logger
        // that is itself down) must not abort the reaction before the re-pull is scheduled, or
        // the lagging mirror would stay stale with no new trigger to heal it.
        try
        {
            onGlitch?.Invoke(firstStamp, secondStamp);
        }
        catch (Exception ex)
        {
            onRepullError?.Invoke(ex);
        }
    }

    private static void RepullLagging<T1, T2>(
        RemoteSignal<T1> first,
        RemoteSignal<T2> second,
        AdoptedPublication<T1> firstPublication,
        AdoptedPublication<T2> secondPublication,
        Action<Exception>? onRepullError)
    {
        // Mirrors on different incarnations of a restarting host are incomparable by design
        // (and dominance over an honestly-empty stamp would misread the empty side as lagging):
        // re-pull BOTH -- a pull returns the host's current truth, so both sides converge on it
        // and the barrier renders on the next consistent snapshot. Re-pulling only an arbitrary
        // side would loop forever exactly in the restart case.
        if (firstPublication.Epoch != secondPublication.Epoch)
        {
            Schedule(() => first.PullAsync(), onRepullError);
            Schedule(() => second.PullAsync(), onRepullError);
            return;
        }

        // Same incarnation: dominance tells which side lags when the stamps are comparable.
        // When neither dominates (both straddle a write), re-pull both for the same reason.
        var firstLags = firstPublication.Stamp.IsDominatedBy(secondPublication.Stamp);
        var secondLags = secondPublication.Stamp.IsDominatedBy(firstPublication.Stamp);
        if (firstLags)
        {
            Schedule(() => first.PullAsync(), onRepullError);
        }
        else if (secondLags)
        {
            Schedule(() => second.PullAsync(), onRepullError);
        }
        else
        {
            Schedule(() => first.PullAsync(), onRepullError);
            Schedule(() => second.PullAsync(), onRepullError);
        }
    }

    private static void Schedule(Func<Task> repull, Action<Exception>? onRepullError)
    {
        // The re-pull is bridge work, not reaction work: the barrier evaluates while its flow
        // holds the context lock in UPGRADEABLE mode, and that holdership travels with the
        // ExecutionContext into Task.Run -- an inherited flow reaching Local.Set before the
        // reaction unwinds would be refused as a recursive exclusive acquisition inside the
        // upgradeable scope (a race the catch below would misreport as a transport fault,
        // leaving the mirror stale). Suppressing the flow makes the re-pull a fresh top-level
        // flow that simply queues behind the reaction's lock, like any transport delivery.
        if (ExecutionContext.IsFlowSuppressed())
        {
            Start();
            return;
        }
        using (ExecutionContext.SuppressFlow())
        {
            Start();
        }

        void Start() => _ = Task.Run(async () =>
        {
            try
            {
                await repull();
            }
            catch (Exception ex)
            {
                onRepullError?.Invoke(ex);
            }
        });
    }
}
