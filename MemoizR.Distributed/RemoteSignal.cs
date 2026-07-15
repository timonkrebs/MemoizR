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
///    <see cref="OnPeerReset"/> runs so the bridge can resubscribe;
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
    private readonly SemaphoreSlim adoptionGate = new(1, 1);
    private readonly Func<Task<ValuePayload<T>>> pull;
    private readonly HashSet<long> abandonedEpochs = [];
    private readonly EagerRelativeSignal<T> local;
    private int expectedNodeId;
    private bool nodeIdPinned;
    private long currentEpoch;
    private long lastSequence;
    // Incremented when any epoch change commits (a reset or the first-contact pin). A pull
    // captures the generation at issue, and its answer may commit an epoch change only if the
    // generation is unchanged -- i.e. no epoch change committed since the pull was issued, so
    // a delayed response from a pull issued before the mirror learned the current incarnation
    // can never abandon it. Deliberately NOT bumped at pull issue: overlapping verification
    // pulls must not invalidate each other optimistically, or a failed newer pull would strand
    // an older pull's perfectly live answer until a heartbeat retries.
    private long epochGeneration;
    private volatile AdoptedPublication<T>? publication;

    private enum AdoptionVerdict { Drop, Adopt, AdoptReset, VerifyByPull }

    /// <summary>
    /// The local signal carrying the mirrored value: wire reactions and memos to this. It is
    /// deliberately the READ-ONLY surface -- only the adoption protocol writes the mirror; a
    /// consumer-side Set would desynchronize the graph value from the publication evidence and
    /// the sequence order. Its own causality stamp is LOCAL (peer B's incarnation); the
    /// foreign evidence lives in <see cref="Publication"/> until wire-format v3 lets it splice.
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
    /// only the resubscription itself needs retrying. Back-to-back resets can overlap hook
    /// runs; a resubscribing bridge must tolerate that (or recreate the mirror for a clean
    /// slate). The abandoned-epoch set stays bounded by the number of restarts this mirror
    /// lived through.
    /// </summary>
    public Func<Task>? OnPeerReset { get; init; }

    // Re-run downstream reactions on the CURRENT value and publication: the barrier's
    // affirmation path needs a graph trigger after a re-pull round that adopted nothing (an
    // eager relative signal always propagates, even for an identical value). Harmless against
    // concurrent adoptions -- the publication swap rides inside the adopting Set's own
    // exclusive-locked callback, so this republication observes either the old or the new
    // snapshot whole, never a torn one.
    internal Task RepublishLocalAsync() => local.Set(value => value);

    internal RemoteSignal(EagerRelativeSignal<T> local, Func<Task<ValuePayload<T>>> pull, int? nodeId)
    {
        this.local = local;
        this.pull = pull;
        if (nodeId is { } id)
        {
            expectedNodeId = id;
            nodeIdPinned = true;
        }
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
    /// bus may broadcast advertisements; only value adoption treats misrouting as an error).
    /// </summary>
    public async Task OnStaleAsync(StaleNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        bool shouldPull;
        await adoptionGate.WaitAsync();
        try
        {
            shouldPull = (!nodeIdPinned || notification.NodeId == expectedNodeId)
                && ((publication?.Unverifiable ?? false)
                    || (!abandonedEpochs.Contains(notification.Epoch)
                        && (notification.Epoch != currentEpoch || notification.Sequence > lastSequence)));
        }
        finally
        {
            adoptionGate.Release();
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
        await adoptionGate.WaitAsync();
        try
        {
            verdict = Classify(payload, pulledAtGeneration);
            if (verdict is AdoptionVerdict.Adopt or AdoptionVerdict.AdoptReset)
            {
                var adopted = CommitOrdering(payload, incoming, verdict == AdoptionVerdict.AdoptReset);
                await local.Set(_ =>
                {
                    // The snapshot becomes visible as part of the graph write, under the same
                    // exclusive context lock: a barrier evaluation (upgradeable, excluded by
                    // the exclusive) can never observe the new evidence while the local graph
                    // value still lags behind the queued Set -- publication and graph value
                    // advance together, and the Set's own propagation re-runs the barrier.
                    publication = adopted;
                    return payload.Value;
                });
            }
        }
        finally
        {
            adoptionGate.Release();
        }

        if (verdict == AdoptionVerdict.VerifyByPull)
        {
            await PullAsync();
            return;
        }

        // The resubscription hook runs with the adoption fully committed and the gate free: a
        // hook that pulls or feeds this same mirror re-enters cleanly instead of deadlocking on
        // the gate, and a hook that throws cannot leave the ordering state claiming a value the
        // local signal never published.
        if (verdict == AdoptionVerdict.AdoptReset && OnPeerReset != null)
        {
            await OnPeerReset();
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
        if (nodeIdPinned && payload.NodeId != expectedNodeId)
        {
            throw new ArgumentException($"The value payload describes node {payload.NodeId}, but this mirror is bound to node {expectedNodeId}.", nameof(payload));
        }
        nodeIdPinned = true;
        expectedNodeId = payload.NodeId;

        if (abandonedEpochs.Contains(payload.Epoch))
        {
            return AdoptionVerdict.Drop; // late traffic from a dead incarnation
        }
        if (currentEpoch == 0)
        {
            return AdoptionVerdict.Adopt; // first contact pins the incarnation
        }
        if (payload.Epoch == currentEpoch)
        {
            // The sequence totally orders publications within an epoch; at or below the last
            // adopted one is a late or duplicated delivery.
            return payload.Sequence > lastSequence ? AdoptionVerdict.Adopt : AdoptionVerdict.Drop;
        }

        // An epoch change: only a pull issued after the last committed epoch change may commit
        // it. An unsolicited payload gets a verification pull instead; a superseded pull answer
        // (an epoch change committed since it was issued) is just dropped -- whatever committed
        // is newer knowledge, and the live incarnation keeps advertising.
        if (pulledAtGeneration == Volatile.Read(ref epochGeneration))
        {
            return AdoptionVerdict.AdoptReset;
        }
        return pulledAtGeneration is null ? AdoptionVerdict.VerifyByPull : AdoptionVerdict.Drop;
    }

    // Must be called under the adoption gate. Commits the ordering state and builds the new
    // publication snapshot; the caller makes it visible inside the local graph write.
    private AdoptedPublication<T> CommitOrdering(ValuePayload<T> payload, CausalityStamp incoming, bool isReset)
    {
        CausalityStamp heldVerifiedStamp;
        if (isReset)
        {
            abandonedEpochs.Add(currentEpoch);
            heldVerifiedStamp = CausalityStamp.Empty; // never merged across incarnations
        }
        else
        {
            heldVerifiedStamp = publication?.Stamp ?? CausalityStamp.Empty;
        }

        if (payload.Epoch != currentEpoch)
        {
            // Any committed epoch change (a reset or the first-contact pin) invalidates every
            // in-flight pull: their answers describe a world from before the mirror learned
            // this incarnation, and must not be able to commit another epoch change.
            Interlocked.Increment(ref epochGeneration);
        }

        currentEpoch = payload.Epoch;
        lastSequence = payload.Sequence;
        return new AdoptedPublication<T>(
            payload.Value,
            payload.Epoch,
            payload.Unverifiable ? heldVerifiedStamp : incoming,
            payload.Unverifiable);
    }
}
