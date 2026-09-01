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
    /// verified snapshots, or on a pair a full re-pull round affirmed (see the class doc).
    /// Keep the returned reaction (and the mirrors) strongly referenced. Re-pull faults are
    /// reported to <paramref name="onRepullError"/> (the next advertisement or heartbeat
    /// retries), and so is a throwing <paramref name="onGlitch"/> callback -- diagnostics must
    /// never block the healing path; rendering is skipped until evidence is verified again.
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

        var healer = new Healer<T1, T2>(factory, first, second, onRepullError);
        return factory.BuildReaction("distributed barrier").CreateReaction(
            first.Local, second.Local, healer.AffirmationTick, (_, _, _) =>
            {
                // The graph parameters only TRIGGER the barrier. Rendering reads each mirror's
                // atomic publication instead: the parameter values were captured when the
                // propagation started, while an adoption landing mid-reaction swaps the side
                // evidence -- pairing the two would render an (old value, new stamp) snapshot
                // that never existed. A newer pair than the trigger saw is still a real
                // published pair, and its own propagation re-runs the barrier anyway.
                var firstPublication = first.Publication;
                var secondPublication = second.Publication;
                if (firstPublication is null || secondPublication is null)
                {
                    return; // nothing synced yet
                }

                // An affirmation vouches only for the exact pair its heal round verified;
                // observing any other pair expires it, so its authority never outlives the round.
                var affirmed = healer.MatchOrExpire(firstPublication, secondPublication);

                // Unverifiable evidence means CANNOT VERIFY: no render, never a guess. The
                // pull that heals an unverifiable spell is driven by advertisements and
                // heartbeats, not here.
                if (firstPublication.Unverifiable || secondPublication.Unverifiable)
                {
                    return;
                }

                // Incarnation identity first: stamps from different epochs never agree, but an
                // honestly-EMPTY stamp is epoch-agnostic and vacuously consistent with anything
                // -- without the header-epoch check, a restarted host's first empty publication
                // would render together with the other mirror's pre-restart state.
                var consistent = firstPublication.Epoch == secondPublication.Epoch
                    && firstPublication.Stamp.IsConsistentWith(secondPublication.Stamp);
                if (!consistent && !affirmed)
                {
                    ReportGlitch(firstPublication.Stamp, secondPublication.Stamp, onGlitch, onRepullError);
                    healer.RepullLagging(firstPublication, secondPublication);
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

    // The heal loop of one barrier: re-pulls, the round-scoped affirmation, and the barrier's
    // own re-trigger.
    private sealed class Healer<T1, T2>
    {
        private readonly RemoteSignal<T1> first;
        private readonly RemoteSignal<T2> second;
        private readonly Action<Exception>? onRepullError;
        private long rounds;

        // The affirmed pair, compared by CONTENT (the record's value equality): a non-memoized
        // export (a ConcurrentRace re-races on every pull) answers a re-pull with an
        // equal-content publication under a fresh sequence, and reference identity would
        // refuse the affirmation every round and spin the heal loop forever; every real change
        // still counts as a change. Volatile: written on the heal flow, read by the reaction.
        private volatile Tuple<AdoptedPublication<T1>, AdoptedPublication<T2>>? affirmed;

        // An affirmation is knowledge the graph does not carry, so the barrier owns a private
        // third input to re-run itself on. Writing the mirror's local signal instead would
        // advance its local stamp and dirty every consumer memo over it for nothing.
        internal Signal<long> AffirmationTick { get; }

        internal Healer(MemoFactory factory, RemoteSignal<T1> first, RemoteSignal<T2> second, Action<Exception>? onRepullError)
        {
            this.first = first;
            this.second = second;
            this.onRepullError = onRepullError;
            AffirmationTick = factory.CreateSignal("distributed barrier affirmation", 0L);
        }

        internal bool MatchOrExpire(AdoptedPublication<T1> firstPublication, AdoptedPublication<T2> secondPublication)
        {
            var pair = affirmed;
            if (pair != null && Equals(pair.Item1, firstPublication) && Equals(pair.Item2, secondPublication))
            {
                return true;
            }
            affirmed = null;
            return false;
        }

        internal void RepullLagging(AdoptedPublication<T1> firstPublication, AdoptedPublication<T2> secondPublication)
        {
            // Re-pull only the dominated side when exactly one lags; both otherwise. Mirrors on
            // different incarnations are incomparable by design (and dominance over an
            // honestly-empty stamp would misread the empty side as lagging), and when neither
            // dominates both straddle a write -- a pull returns the host's current truth, so
            // both sides converge on it, whereas re-pulling one arbitrary side would loop
            // forever exactly in the restart case.
            var sameEpoch = firstPublication.Epoch == secondPublication.Epoch;
            var firstLags = sameEpoch && firstPublication.Stamp.IsDominatedBy(secondPublication.Stamp);
            var secondLags = sameEpoch && secondPublication.Stamp.IsDominatedBy(firstPublication.Stamp);

            // Bridge work, not reaction work: the barrier evaluates while its flow holds the
            // context lock in UPGRADEABLE mode, and an inherited flow reaching Local.Set before
            // the reaction unwinds would be refused as a recursive exclusive acquisition.
            DetachedFlow.Run(
                () => HealAsync(firstPublication, secondPublication, pullFirst: firstLags || !secondLags, pullSecond: secondLags || !firstLags),
                ex => onRepullError?.Invoke(ex));
        }

        private async Task HealAsync(
            AdoptedPublication<T1> firstPublication,
            AdoptedPublication<T2> secondPublication,
            bool pullFirst,
            bool pullSecond)
        {
            await PullAsync(pullFirst, pullSecond);
            if (!Unchanged())
            {
                return; // an adoption landed; its own propagation re-runs the barrier
            }

            // A one-sided round only proves half the pair: verify the other side by pull too
            // before declaring the whole pair affirmed. Cross-epoch pairs are never affirmed.
            await PullAsync(!pullFirst, !pullSecond);
            if (!Unchanged() || firstPublication.Epoch != secondPublication.Epoch)
            {
                return;
            }

            affirmed = Tuple.Create(firstPublication, secondPublication);
            await AffirmationTick.Set(Interlocked.Increment(ref rounds));

            bool Unchanged() =>
                Equals(first.Publication, firstPublication)
                && Equals(second.Publication, secondPublication);
        }

        // The two mirrors have separate gates and each adoption mints its own flow scope, so
        // pulling both is the same as two concurrent transport deliveries.
        private Task PullAsync(bool pullFirst, bool pullSecond) => (pullFirst, pullSecond) switch
        {
            (true, true) => Task.WhenAll(first.PullAsync(), second.PullAsync()),
            (true, false) => first.PullAsync(),
            (false, true) => second.PullAsync(),
            _ => Task.CompletedTask,
        };
    }
}
