using MemoizR.Reactive;

namespace MemoizR.Distributed;

/// <summary>
/// The glitch barrier over mirrored inputs: renders only on a CONSISTENT, VERIFIED snapshot of
/// the host's write history, and drives recovery itself -- when the two mirrors' evidence
/// disagrees on a shared signal (or the mirrors sit on different incarnations of the host),
/// the lagging side is re-pulled on the bridge's own flow (a reaction body must never Set its
/// own graph's signals, so the re-pull is scheduled outside the evaluation) and the barrier
/// re-runs when the adoption lands. A re-pull round that changes NOTHING -- both hosts answer
/// with exactly the publications the glitch was detected on -- is both hosts AFFIRMING their
/// current truth: the disagreement is then the core's documented conservative stamp
/// under-claim (a scan-skipped recompute keeps an older stamp for a still-valid value), not a
/// lag, and the affirmed pair renders instead of blocking forever. v0 combines mirrors of the
/// SAME host graph; barriers across different peers need the wire-v3 multi-peer epoch table.
/// </summary>
public static class DistributedBarrier
{
    /// <summary>
    /// A reaction over two mirrors that calls <paramref name="render"/> exactly on consistent
    /// verified snapshots -- or on a stamp-inconsistent pair that a full re-pull round
    /// AFFIRMED (each side was verified clean by its own pull answering with the identical
    /// publication, so the values form a real snapshot and only the conservative stamp
    /// under-claim disagrees). Keep the returned reaction (and the mirrors) strongly
    /// referenced. Re-pull faults are reported to <paramref name="onRepullError"/> (the next
    /// advertisement or heartbeat retries), and so is a throwing <paramref name="onGlitch"/>
    /// callback -- diagnostics must never block the healing path; rendering is skipped until
    /// evidence is verified again.
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

        var affirmation = new AffirmationState<T1, T2>();
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
                    if (affirmation.IsAffirmed(firstPublication, secondPublication))
                    {
                        // Both hosts answered a full re-pull round with exactly these
                        // publications: the values are their affirmed current truths and the
                        // stamp disagreement is the documented under-claim -- render, or the
                        // barrier would stay blocked until an unrelated value change.
                        render(firstPublication.Value, secondPublication.Value);
                        return;
                    }

                    ReportGlitch(firstPublication.Stamp, secondPublication.Stamp, onGlitch, onRepullError);
                    RepullLagging(first, second, firstPublication, secondPublication, affirmation, onRepullError);
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
        AffirmationState<T1, T2> affirmation,
        Action<Exception>? onRepullError)
    {
        // Mirrors on different incarnations of a restarting host are incomparable by design
        // (and dominance over an honestly-empty stamp would misread the empty side as lagging):
        // re-pull BOTH -- a pull returns the host's current truth, so both sides converge on it
        // and the barrier renders on the next consistent snapshot. Re-pulling only an arbitrary
        // side would loop forever exactly in the restart case. Within one incarnation,
        // dominance tells which side lags when the stamps are comparable; when neither
        // dominates (both straddle a write), re-pull both for the same reason.
        var crossEpoch = firstPublication.Epoch != secondPublication.Epoch;
        var firstLags = !crossEpoch && firstPublication.Stamp.IsDominatedBy(secondPublication.Stamp);
        var secondLags = !crossEpoch && secondPublication.Stamp.IsDominatedBy(firstPublication.Stamp);
        var pullSecondOnly = !crossEpoch && secondLags && !firstLags;
        var pullFirstOnly = !crossEpoch && firstLags && !secondLags;

        // The re-pull is bridge work, not reaction work: the barrier evaluates while its flow
        // holds the context lock in UPGRADEABLE mode, and an inherited flow reaching Local.Set
        // before the reaction unwinds would be refused as a recursive exclusive acquisition (a
        // race the heal's catch would misreport as a transport fault, leaving the mirror
        // stale). DetachedFlow gives it a fresh top-level flow that simply queues behind the
        // reaction's lock, like any transport delivery.
        DetachedFlow.Run(() => HealAsync(
            first, second, firstPublication, secondPublication,
            pullFirst: !pullSecondOnly, pullSecond: !pullFirstOnly,
            affirmation, onRepullError));
    }

    private static async Task HealAsync<T1, T2>(
        RemoteSignal<T1> first,
        RemoteSignal<T2> second,
        AdoptedPublication<T1> firstPublication,
        AdoptedPublication<T2> secondPublication,
        bool pullFirst,
        bool pullSecond,
        AffirmationState<T1, T2> affirmation,
        Action<Exception>? onRepullError)
    {
        try
        {
            if (pullFirst)
            {
                await first.PullAsync();
            }
            if (pullSecond)
            {
                await second.PullAsync();
            }
            if (!Unchanged())
            {
                return; // an adoption landed; its own propagation re-runs the barrier
            }

            // The dominance-picked side answered with the identical publication. A one-sided
            // round only proves half the pair, so verify the other side by pull too before
            // declaring the whole pair affirmed.
            if (!pullFirst)
            {
                await first.PullAsync();
            }
            if (!pullSecond)
            {
                await second.PullAsync();
            }
            if (!Unchanged() || firstPublication.Epoch != secondPublication.Epoch)
            {
                return;
            }

            affirmation.Affirm(firstPublication, secondPublication);
            // The affirmation is knowledge the graph does not carry: re-run the barrier
            // through the ordinary propagation (an eager signal always re-propagates).
            await first.RepublishLocalAsync();
        }
        catch (Exception ex)
        {
            onRepullError?.Invoke(ex);
        }

        // Content equality, not reference: a non-memoized export (a ConcurrentRace re-races on
        // every pull) answers a re-pull with an EQUAL-content publication under a fresh
        // sequence, and the mirror's adoption swaps the reference -- reference identity would
        // refuse the affirmation every round and spin the heal loop forever. The publication
        // record's value equality (value, epoch, stamp, verifiability) still counts every real
        // change as a change.
        bool Unchanged() =>
            Equals(first.Publication, firstPublication)
            && Equals(second.Publication, secondPublication);
    }

    // The publication pair a full re-pull round affirmed, compared by CONTENT (the record's
    // value equality): any adoption that actually changes value or evidence invalidates a
    // stale affirmation, while an equal-content republication (a re-racing export) keeps the
    // pair affirmed -- which is exactly what the round proved. Volatile: written on the heal
    // flow, read by the barrier reaction.
    private sealed class AffirmationState<T1, T2>
    {
        private volatile Tuple<AdoptedPublication<T1>, AdoptedPublication<T2>>? pair;

        internal void Affirm(AdoptedPublication<T1> first, AdoptedPublication<T2> second) =>
            pair = Tuple.Create(first, second);

        internal bool IsAffirmed(AdoptedPublication<T1> first, AdoptedPublication<T2> second)
        {
            var affirmed = pair;
            return affirmed != null
                && Equals(affirmed.Item1, first)
                && Equals(affirmed.Item2, second);
        }
    }
}
