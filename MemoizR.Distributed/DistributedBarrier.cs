using MemoizR.Reactive;

namespace MemoizR.Distributed;

/// <summary>
/// The glitch barrier over mirrored inputs: renders only on a CONSISTENT, VERIFIED snapshot of
/// the host's write history, and drives recovery itself -- when the two mirrors' evidence
/// disagrees on a shared signal, the lagging side is re-pulled on the bridge's own flow (a
/// reaction body must never Set its own graph's signals, so the re-pull is scheduled outside
/// the evaluation) and the barrier re-runs when the adoption lands. v0 combines mirrors of the
/// SAME host graph; barriers across different peers need the wire-v3 multi-peer epoch table.
/// </summary>
public static class DistributedBarrier
{
    /// <summary>
    /// A reaction over two mirrors that calls <paramref name="render"/> exactly on consistent
    /// verified snapshots. Keep the returned reaction (and the mirrors) strongly referenced.
    /// Re-pull faults are reported to <paramref name="onRepullError"/> (the next advertisement
    /// or heartbeat retries); rendering is skipped until evidence is verified again.
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
            first.Local, second.Local, (firstValue, secondValue) =>
            {
                // Absent evidence (nothing synced yet) and unverifiable evidence mean the same
                // thing to a consumer: CANNOT VERIFY -- no render, never a guess. The pull that
                // heals an unverifiable spell is driven by advertisements/heartbeats, not here.
                if (!first.HasEvidence || !second.HasEvidence || first.Unverifiable || second.Unverifiable)
                {
                    return;
                }

                var firstStamp = first.RemoteStamp;
                var secondStamp = second.RemoteStamp;
                if (!firstStamp.IsConsistentWith(secondStamp))
                {
                    // Diagnostics only -- the barrier heals itself below.
                    onGlitch?.Invoke(firstStamp, secondStamp);
                    RepullLagging(first, second, firstStamp, secondStamp, onRepullError);
                    return;
                }

                render(firstValue, secondValue);
            });
    }

    private static void RepullLagging<T1, T2>(
        RemoteSignal<T1> first,
        RemoteSignal<T2> second,
        CausalityStamp firstStamp,
        CausalityStamp secondStamp,
        Action<Exception>? onRepullError)
    {
        // Dominance tells which side lags when the stamps are comparable. When NEITHER
        // dominates -- both straddle a write, or the mirrors sit on different incarnations of a
        // restarting host (cross-epoch stamps are incomparable by design) -- re-pull BOTH: a
        // pull returns the host's current truth, so both sides converge on it and the barrier
        // renders on the next consistent snapshot. Re-pulling only an arbitrary side would loop
        // forever exactly in the restart case.
        var firstLags = firstStamp.IsDominatedBy(secondStamp);
        var secondLags = secondStamp.IsDominatedBy(firstStamp);
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
        _ = Task.Run(async () =>
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
