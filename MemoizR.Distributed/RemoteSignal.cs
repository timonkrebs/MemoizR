namespace MemoizR.Distributed;

/// <summary>
/// The consumer side of one exported node: a local <see cref="EagerRelativeSignal{T}"/> (so the
/// consumer's graph reacts through the ordinary machinery -- eager on purpose, because an
/// adoption can change the EVIDENCE while the numeric value stays identical, and the barrier
/// must re-run to observe it) plus the foreign evidence the current value arrived with, plus
/// the adoption protocol that makes reordered, at-least-once transports harmless:
///
///  - late traffic from an incarnation this mirror already left is dropped (epochs are random
///    identifiers, not ordered -- the mirror remembers what it abandoned instead of trusting a
///    mismatch to mean "newer");
///  - a different, non-abandoned epoch is a peer RESET: held evidence is discarded, never
///    merged, and <see cref="OnPeerReset"/> runs so the bridge can resubscribe;
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
    private long currentEpoch;
    private long lastSequence;
    private volatile CausalityStamp remoteStamp = CausalityStamp.Empty;
    private volatile bool unverifiable;
    private volatile bool hasEvidence;

    /// <summary>
    /// The local signal carrying the mirrored value: wire reactions and memos to this. Its own
    /// causality stamp is LOCAL (peer B's incarnation); the foreign evidence lives in
    /// <see cref="RemoteStamp"/> until wire-format v3 lets it splice.
    /// </summary>
    public EagerRelativeSignal<T> Local { get; }

    /// <summary>The stamp of the last VERIFIED adopted publication (empty until one arrives).</summary>
    public CausalityStamp RemoteStamp => remoteStamp;

    /// <summary>Whether the host's current publication is one nobody can vouch for.</summary>
    public bool Unverifiable => unverifiable;

    /// <summary>Whether any payload was adopted at all (an honestly-empty stamp is real evidence).</summary>
    public bool HasEvidence => hasEvidence;

    /// <summary>
    /// Runs (awaited, inside the adoption) when a payload reveals a peer RESET -- the hook for
    /// resubscribing on a real transport. The abandoned-epoch set stays bounded by the number
    /// of restarts this mirror lived through; a resubscribing bridge can recreate the mirror
    /// instead if it wants a clean slate.
    /// </summary>
    public Func<Task>? OnPeerReset { get; init; }

    internal RemoteSignal(EagerRelativeSignal<T> local, Func<Task<ValuePayload<T>>> pull)
    {
        Local = local;
        this.pull = pull;
    }

    /// <summary>Pull the host's current truth and adopt it (subject to the ordering rules).</summary>
    public async Task PullAsync() => await OnValueAsync(await pull());

    /// <summary>
    /// Handle a stale advertisement: pull when it is not provably old news -- an unknown or
    /// different epoch (a reset is discovered on the payload), a sequence above the last
    /// adopted one, or any advertisement at all while the mirror is unverifiable.
    /// </summary>
    public async Task OnStaleAsync(StaleNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        bool shouldPull;
        await adoptionGate.WaitAsync();
        try
        {
            shouldPull = unverifiable
                || (!abandonedEpochs.Contains(notification.Epoch)
                    && (notification.Epoch != currentEpoch || notification.Sequence > lastSequence));
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

    /// <summary>Adopt one delivered publication (or drop it, per the ordering rules above).</summary>
    public async Task OnValueAsync(ValuePayload<T> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // Validate the hostile parts up front, outside the gate: the stamp deserializer rejects
        // malformed input, and a zero epoch on a VALUE payload is a protocol violation (hosts
        // always have a nonzero incarnation; only stamps can be honestly epoch-agnostic).
        var incoming = CausalityStamp.Deserialize(payload.Stamp);
        if (payload.Epoch == 0)
        {
            throw new ArgumentException("A value payload must carry the host's nonzero incarnation epoch.", nameof(payload));
        }

        await adoptionGate.WaitAsync();
        try
        {
            if (abandonedEpochs.Contains(payload.Epoch))
            {
                return; // late traffic from a dead incarnation
            }

            var isReset = currentEpoch != 0 && payload.Epoch != currentEpoch;
            if (isReset)
            {
                abandonedEpochs.Add(currentEpoch);
                currentEpoch = 0;
                lastSequence = 0;
                remoteStamp = CausalityStamp.Empty; // never merged across incarnations
            }
            else if (currentEpoch != 0 && payload.Sequence <= lastSequence)
            {
                return; // late or duplicated delivery: the sequence totally orders publications within an epoch
            }

            currentEpoch = payload.Epoch;
            lastSequence = payload.Sequence;
            hasEvidence = true;
            unverifiable = payload.Unverifiable;
            if (!payload.Unverifiable)
            {
                remoteStamp = incoming;
            }

            if (isReset && OnPeerReset != null)
            {
                await OnPeerReset();
            }

            await Local.Set(_ => payload.Value);
        }
        finally
        {
            adoptionGate.Release();
        }
    }
}
