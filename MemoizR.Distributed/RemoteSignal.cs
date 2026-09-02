using Nito.AsyncEx;

namespace MemoizR.Distributed;

/// <summary>
/// One adopted publication as the barrier must observe it: the mirrored value and the evidence
/// it arrived with, bound in a single immutable snapshot (swapped atomically, so a reader can
/// never pair one publication's value with another's evidence). <see cref="Epoch"/> is the
/// header epoch of the adopted payload -- carried beside the stamp because an honestly-empty
/// stamp is epoch-agnostic, and incarnation identity must survive empty publications.
/// <see cref="Stamp"/> is the stamp of the last VERIFIED adoption (an unverifiable adoption
/// carries the held verified stamp forward, with <see cref="Unverifiable"/> set).
/// </summary>
public sealed record AdoptedPublication<T>(T Value, long Epoch, CausalityStamp Stamp, bool Unverifiable);

/// <summary>
/// The consumer side of one exported node: a local eager signal (readable through
/// <see cref="Local"/> -- eager on purpose, because an adoption can change the EVIDENCE while
/// the numeric value stays identical, and the barrier must re-run to observe it) plus the
/// foreign evidence the current value arrived with, plus the adoption protocol that makes
/// reordered, at-least-once transports harmless:
///
///  - a payload for a different node than this mirror is bound to is a routing violation and
///    is rejected (the binding comes from the creation-time node id, or is pinned by the
///    first delivered payload) -- adopting it would replace the value with a sibling export's
///    and advance the sequence order with a foreign counter;
///  - late traffic from an incarnation this mirror already left is dropped (epochs are random
///    identifiers, not ordered -- the mirror remembers what it abandoned instead of trusting a
///    mismatch to mean "newer");
///  - a different, non-abandoned epoch signals a peer RESET, but it is committed only by a
///    payload answering a pull this mirror issued after its last committed epoch change: with
///    unordered epochs, a delayed payload from a skipped dead incarnation is indistinguishable
///    by inspection from a live restart, and adopting it would abandon the LIVE epoch and
///    wedge the mirror. An unsolicited epoch-mismatch payload is therefore discarded and
///    answered with a verification pull (the response reflects the incarnation that is
///    actually alive), and committing an epoch change invalidates the answers of all pulls
///    still in flight. On a committed reset, held evidence is discarded, never merged, and
///    <see cref="OnPeerReset"/> runs so the bridge can resubscribe -- and while that
///    resubscription is in flight, epoch-changing answers are refused entirely (they may have
///    travelled the dead incarnation's channel), with pulls issued inside the window
///    invalidated when it closes and any refusal re-verified by one fresh pull then (the peer
///    may have restarted again mid-resubscription, and that delivery may have been the new
///    incarnation's only advertisement);
///  - within an epoch, the publication SEQUENCE totally orders deliveries: anything at or
///    below the last adopted sequence is a late or duplicated delivery and is dropped --
///    including the dependency-oscillation shapes causality stamps cannot order, and equally
///    while the mirror is unverifiable (a recovery is a NEW publication with a higher
///    sequence, so it is never mistaken for old news);
///  - an UNVERIFIABLE payload is adopted like any other (the consumer must stop trusting held
///    evidence now), but the held stamp of the last verified adoption is kept for the barrier
///    until a verified payload replaces it.
///
/// A future wire-format v3 (multi-peer epoch table) will let this evidence splice into LOCAL
/// stamps; until then it travels beside the graph and the barrier checks it explicitly.
/// </summary>
public sealed class RemoteSignal<T>
{
    private readonly AsyncLock adoptionGate = new();
    private readonly Func<Task<ValuePayload<T>>> pull;
    private readonly HashSet<long> abandonedEpochs = [];
    private readonly EagerRelativeSignal<T> local;
    private int? boundNodeId;
    private long lastSequence;
    private volatile AdoptedPublication<T>? publication;
    private volatile Exception? lastBackgroundError;

    // Incremented when any epoch change commits (a reset or the first-contact pin), and again
    // when a reset's resubscription window closes. A pull captures the generation at issue,
    // and its answer may commit an epoch change only if the generation is unchanged -- so a
    // delayed response from a pull issued before the mirror learned the current incarnation
    // can never abandon it. Deliberately NOT bumped at pull issue: overlapping verification
    // pulls must not invalidate each other optimistically, or a failed newer pull would strand
    // an older pull's perfectly live answer until a heartbeat retries.
    private long epochGeneration;

    // Gate-guarded trust in the pull channel (see the reset bullet of the class doc). Only
    // these four states are reachable: a refused epoch exists only inside a window, and a
    // window can only open from a trusted or suspect channel. Mirrors without a hook stay
    // Trusted forever -- declaring no resubscription means the channel is address-stable, so
    // a fresh pull's answer always reflects whoever is actually alive.
    private enum ChannelState
    {
        Trusted,
        // The last window closed on a FAILED hook: the channel may still be routed to a dead
        // incarnation, so superseded stale answers (zero fresh evidence) never solicit it.
        Suspect,
        // A reset committed and its OnPeerReset is in flight: epoch-changing answers are
        // refused, since they may have travelled the not-yet-resubscribed channel.
        Resettling,
        // ... and an epoch change was refused (or a foreign restart advertised) meanwhile:
        // closing the window follows it up with one verification pull.
        ResettlingRefusedEpoch,
    }
    private ChannelState channel;

    private enum AdoptionVerdict { Drop, Adopt, AdoptReset, VerifyByPull }

    private long CurrentEpoch => publication?.Epoch ?? 0;

    /// <summary>
    /// The last error a background recovery step threw -- the queued verification pull after
    /// a resubscription window closed, including an <see cref="OnPeerReset"/> failure for a
    /// reset that background pull committed. Background steps have no delivering caller to
    /// surface to, so failures land here (like <c>ExportedNode.LastPublishError</c>); a bridge
    /// that must react promptly to resubscription failures should observe its own hook.
    /// Transport faults recorded here are self-healing -- the next advertisement or heartbeat
    /// retries.
    /// </summary>
    public Exception? LastBackgroundError => lastBackgroundError;

    /// <summary>
    /// The local signal carrying the mirrored value: wire reactions and memos to this. It is
    /// deliberately the READ-ONLY surface -- only the adoption protocol writes the mirror; a
    /// consumer-side Set would desynchronize the graph value from the publication evidence and
    /// the sequence order. Its own causality stamp is LOCAL (peer B's incarnation); the
    /// foreign evidence lives in <see cref="Publication"/> until wire-format v3 lets it
    /// splice -- which is also why re-exporting this signal is refused: its local stamps
    /// carry none of the origin's evidence.
    /// </summary>
    public IStampedGetR<T> Local => local;

    /// <summary>
    /// The last adopted publication -- value and evidence as ONE atomic snapshot (null until a
    /// payload is adopted). The barrier renders from this, never from the graph value plus a
    /// separately-read stamp: those two reads could straddle an adoption and pair a value with
    /// evidence it never published under.
    /// </summary>
    public AdoptedPublication<T>? Publication => publication;

    /// <summary>The stamp of the last VERIFIED adopted publication (empty until one arrives).</summary>
    public CausalityStamp RemoteStamp => publication?.Stamp ?? CausalityStamp.Empty;

    /// <summary>Whether the host's current publication is one nobody can vouch for.</summary>
    public bool Unverifiable => publication?.Unverifiable ?? false;

    /// <summary>Whether any payload was adopted at all (an honestly-empty stamp is real evidence).</summary>
    public bool HasEvidence => publication != null;

    /// <summary>
    /// Runs (awaited by the delivering call) when a payload commits a peer RESET -- the hook
    /// for resubscribing on a real transport. It is invoked AFTER the reset adoption committed
    /// and the adoption gate is released: the hook may freely pull or feed this same mirror
    /// (re-entering the adoption path), and if it throws, the failure surfaces to the
    /// delivering caller while the value stays adopted -- redeliveries drop as duplicates and
    /// only the resubscription itself needs retrying. Hook runs are SINGLE-FLIGHT: while one
    /// is in flight, no further epoch change can commit, so back-to-back restarts run their
    /// hooks strictly in sequence. A reset is observed per mirror: a restarted host invalidates
    /// every mirror of it, and the bridge resubscribes them together. The abandoned-epoch set
    /// stays bounded by the number of restarts this mirror lived through.
    /// </summary>
    public Func<Task>? OnPeerReset { get; init; }

    internal RemoteSignal(EagerRelativeSignal<T> local, Func<Task<ValuePayload<T>>> pull, int? nodeId)
    {
        this.local = local;
        this.pull = pull;
        boundNodeId = nodeId;
    }

    /// <summary>Pull the host's current truth and adopt it (subject to the ordering rules).</summary>
    public async Task PullAsync()
    {
        var generation = Volatile.Read(ref epochGeneration);
        await AdoptAsync(await pull(), generation);
    }

    /// <summary>
    /// Handle a stale advertisement: pull when it is not provably old news -- an unknown or
    /// different epoch (a reset is discovered on the pulled payload), a sequence above the
    /// last adopted one, or any advertisement at all while the mirror is unverifiable. An
    /// advertisement for a different node than this mirror is bound to is ignored (a fan-out
    /// bus may broadcast advertisements; only value adoption treats misrouting as an error),
    /// and a restart advertised while a resubscription window is open is remembered for the
    /// window's closing verification pull instead of chased on the not-yet-resubscribed channel.
    /// </summary>
    public async Task OnStaleAsync(StaleNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        bool shouldPull;
        using (await adoptionGate.LockAsync())
        {
            var foreignLiveEpoch = notification.Epoch != CurrentEpoch && !abandonedEpochs.Contains(notification.Epoch);
            if (boundNodeId is { } bound && notification.NodeId != bound)
            {
                shouldPull = false;
            }
            else if (foreignLiveEpoch && channel is ChannelState.Resettling or ChannelState.ResettlingRefusedEpoch)
            {
                channel = ChannelState.ResettlingRefusedEpoch; // the advertisement is the restart evidence
                shouldPull = false;
            }
            else
            {
                shouldPull = Unverifiable || foreignLiveEpoch
                    || (notification.Epoch == CurrentEpoch && notification.Sequence > lastSequence);
            }
        }

        if (shouldPull)
        {
            await PullAsync();
        }
    }

    /// <summary>
    /// Adopt one delivered publication (or drop it, per the ordering rules above). A payload
    /// revealing an epoch change is not adopted directly: it is discarded and the mirror
    /// issues a verification pull, whose answer commits whatever incarnation is actually
    /// alive.
    /// </summary>
    public Task OnValueAsync(ValuePayload<T> payload) => AdoptAsync(payload, pulledAtGeneration: null);

    private async Task AdoptAsync(ValuePayload<T> payload, long? pulledAtGeneration)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var incoming = ValidateProtocol(payload);

        AdoptionVerdict verdict;
        using (await adoptionGate.LockAsync())
        {
            verdict = Classify(payload, pulledAtGeneration);
            if (verdict is AdoptionVerdict.Adopt or AdoptionVerdict.AdoptReset)
            {
                var isReset = verdict == AdoptionVerdict.AdoptReset;
                await local.Set(_ =>
                {
                    // Ordering state and snapshot commit atomically WITH the graph write, under
                    // the same exclusive context lock: a barrier evaluation (upgradeable,
                    // excluded by the exclusive) can never observe the new evidence while the
                    // local graph value still lags behind the queued Set, and a Set that fails
                    // to acquire at all (a bridge delivering from a same-context reaction flow
                    // is refused as a recursive acquisition) leaves the ordering untouched --
                    // the redelivery must adopt, not be duplicate-dropped against a sequence
                    // the graph never published.
                    publication = CommitOrdering(payload, incoming, isReset);
                    return payload.Value;
                });

                if (pulledAtGeneration is not null && channel == ChannelState.Suspect)
                {
                    channel = ChannelState.Trusted; // the channel served an answer this mirror adopted
                }
            }
        }

        if (verdict == AdoptionVerdict.VerifyByPull)
        {
            await PullAsync();
            return;
        }

        // The hook runs with the adoption fully committed and the gate free, so it may
        // re-enter this mirror; its window closes whether it succeeded or threw (either way
        // the delivering caller knows).
        if (verdict == AdoptionVerdict.AdoptReset && OnPeerReset != null)
        {
            var resubscribed = false;
            try
            {
                await OnPeerReset();
                resubscribed = true;
            }
            finally
            {
                await CloseResettlingWindowAsync(resubscribed);
            }
        }
    }

    // Closes the window and invalidates every pull issued inside it. A refused epoch change
    // is followed up with one detached verification pull -- only after a SUCCESSFUL
    // resubscription: a failed hook just reported the channel broken, and actively pulling it
    // would solicit exactly the stale answers the window exists to refuse; recovery is then
    // the bridge's own retry plus the next advertisement or heartbeat.
    private async Task CloseResettlingWindowAsync(bool resubscribed)
    {
        bool verify;
        using (await adoptionGate.LockAsync())
        {
            verify = channel == ChannelState.ResettlingRefusedEpoch && resubscribed;
            channel = resubscribed ? ChannelState.Trusted : ChannelState.Suspect;
            Interlocked.Increment(ref epochGeneration);
        }

        if (verify)
        {
            // No delivering caller to surface to: recorded, not swallowed -- this can be an
            // OnPeerReset failure for the reset this pull commits, which the bridge must be
            // able to learn about.
            DetachedFlow.Run(PullAsync, ex => lastBackgroundError = ex);
        }
    }

    // Rejects the hostile parts up front, outside the gate: the stamp deserializer rejects
    // malformed input, and a zero epoch on a VALUE payload is a protocol violation (hosts
    // always have a nonzero incarnation; only stamps can be honestly epoch-agnostic).
    private static CausalityStamp ValidateProtocol(ValuePayload<T> payload)
    {
        var incoming = CausalityStamp.Deserialize(payload.Stamp);
        if (payload.Epoch == 0)
        {
            throw new ArgumentException("A value payload must carry the host's nonzero incarnation epoch.", nameof(payload));
        }
        // A non-empty stamp carries its own epoch; it must be the incarnation the header claims,
        // or the ordering (header epoch) and the evidence the barrier compares (stamp epoch)
        // would describe different incarnations. Empty stamps are epoch-agnostic by design.
        if (incoming.Epoch != 0 && incoming.Epoch != payload.Epoch)
        {
            throw new ArgumentException("The payload's stamp belongs to a different incarnation epoch than the payload header claims.", nameof(payload));
        }
        return incoming;
    }

    // Must be called under the adoption gate.
    private AdoptionVerdict Classify(ValuePayload<T> payload, long? pulledAtGeneration)
    {
        if (boundNodeId is { } bound && payload.NodeId != bound)
        {
            throw new ArgumentException($"The value payload describes node {payload.NodeId}, but this mirror is bound to node {bound}.", nameof(payload));
        }
        boundNodeId = payload.NodeId;

        if (abandonedEpochs.Contains(payload.Epoch))
        {
            return AdoptionVerdict.Drop; // late traffic from a dead incarnation
        }
        if (CurrentEpoch == 0)
        {
            return AdoptionVerdict.Adopt; // first contact pins the incarnation
        }
        if (payload.Epoch == CurrentEpoch)
        {
            return payload.Sequence > lastSequence ? AdoptionVerdict.Adopt : AdoptionVerdict.Drop;
        }

        // An epoch change (see the reset bullet of the class doc).
        if (channel is ChannelState.Resettling or ChannelState.ResettlingRefusedEpoch)
        {
            channel = ChannelState.ResettlingRefusedEpoch;
            return AdoptionVerdict.Drop;
        }
        if (pulledAtGeneration == Volatile.Read(ref epochGeneration))
        {
            return AdoptionVerdict.AdoptReset;
        }
        // A SUPERSEDED pull answer (an epoch change committed since it was issued) that still
        // claims a different epoch than the one committed re-verifies too: with racing
        // verification pulls under one generation, the older restart's answer can commit
        // first, and silently dropping the newer restart's answer would pin the mirror to a
        // dead epoch when its delivery was a one-shot. Each re-verify costs one pull and needs
        // a real interleaved commit, so the chain terminates -- unless the channel is suspect,
        // where such a zero-fresh-evidence answer must not solicit it.
        if (pulledAtGeneration is not null && channel == ChannelState.Suspect)
        {
            return AdoptionVerdict.Drop;
        }
        return AdoptionVerdict.VerifyByPull;
    }

    // Must be called under the adoption gate. Commits the ordering state and builds the new
    // publication snapshot; the caller makes it visible inside the local graph write.
    private AdoptedPublication<T> CommitOrdering(ValuePayload<T> payload, CausalityStamp incoming, bool isReset)
    {
        // Held evidence is never merged across incarnations.
        var heldVerifiedStamp = isReset ? CausalityStamp.Empty : publication?.Stamp ?? CausalityStamp.Empty;
        if (isReset)
        {
            abandonedEpochs.Add(CurrentEpoch);
            if (OnPeerReset != null)
            {
                channel = ChannelState.Resettling;
            }
        }

        if (payload.Epoch != CurrentEpoch)
        {
            // Any committed epoch change (a reset or the first-contact pin) invalidates every
            // in-flight pull: their answers describe a world from before the mirror learned
            // this incarnation, and must not be able to commit another epoch change.
            Interlocked.Increment(ref epochGeneration);
        }

        lastSequence = payload.Sequence;
        return new AdoptedPublication<T>(
            payload.Value,
            payload.Epoch,
            payload.Unverifiable ? heldVerifiedStamp : incoming,
            payload.Unverifiable);
    }
}
