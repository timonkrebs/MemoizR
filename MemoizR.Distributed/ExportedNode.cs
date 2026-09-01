using MemoizR.Reactive;

namespace MemoizR.Distributed;

/// <summary>
/// The host side of one exported node: pushes a <see cref="StaleNotification"/> whenever the
/// node's value advances (an export is just a reaction), answers pulls with the untorn
/// (value, evidence, sequence) triple of one publication, and optionally re-advertises on a
/// heartbeat -- the local invalidation cascade propagates only value CHANGES, so evidence-only
/// transitions (a stamp refresh, an unverifiable spell healing) are discovered by consumers
/// pulling after a heartbeat, by design. Dispose stops the export reaction and the heartbeat.
/// </summary>
public sealed class ExportedNode<T> : IDisposable
{
    private readonly MemoHandlR<T> node;
    private readonly Func<StaleNotification, Task> publishStale;
    private Reaction? exportReaction;
    private ITimer? heartbeat;
    private volatile Exception? lastPublishError;

    /// <summary>The exported node's stable per-context id (what consumers key mirrors by).</summary>
    public int NodeId => node.Id;

    /// <summary>
    /// The last error the stale-publishing callback threw, if any. Publishing is
    /// fire-and-forget from the export reaction (a transport hiccup must not fault the graph),
    /// so failures land here instead of being thrown; the next value change or heartbeat
    /// re-advertises, which makes lost notifications self-healing on a live graph.
    /// </summary>
    public Exception? LastPublishError => lastPublishError;

    internal ExportedNode(MemoHandlR<T> node, Func<StaleNotification, Task> publishStale)
    {
        this.node = node;
        this.publishStale = publishStale;
    }

    internal void AttachReaction(Reaction reaction) => exportReaction = reaction;

    /// <summary>
    /// Answer a pull with the node's CURRENT truth: recompute if stale, then return the
    /// (value, evidence, sequence) triple of one publication -- never torn. A concurrent
    /// publication between the read and the box snapshot only makes the answer newer, which a
    /// pull is always allowed to be. Faults propagate to the caller (the transport decides
    /// retry policy); the consumer's mirror is left unchanged by a failed pull.
    /// </summary>
    public async Task<ValuePayload<T>> PullAsync()
    {
        await node.ReadWithEvidence();

        // The export-time chain check can miss a LAZY memo whose sources only wire on first
        // evaluation (and dynamic rewiring can splice a mirror in later): the pull is the
        // wire egress, so it re-checks against the now-wired chain -- the read above just
        // evaluated the node, so the wiring is current.
        if (MirrorLocals.TouchesMirrorLocal(node))
        {
            throw new InvalidOperationException(MirrorLocals.ReExportMessage);
        }

        if (node.Evidence.Unverifiable || node.stateCell.State != CacheState.CacheClean)
        {
            // Two shapes the plain read cannot answer honestly, both cured by ONE fresh
            // evaluation of the suspect chain (through the ordinary invalidation entry --
            // exactly what an upstream write would do):
            //
            //  - An UNVERIFIABLE publication can be sticky while the node is lazily clean:
            //    contagion from a since-healed source survives an equal-value parent scan, so
            //    every heartbeat-driven pull would answer the same no-claim publication and
            //    sequence, which the mirror drops as a duplicate -- unverifiable forever.
            //  - A FAULT-PARKED read: a dependency faulted during the parent scan, so the
            //    read served the last good VERIFIED box and left the node non-clean. Shipping
            //    that as the current truth would let a new mirror adopt stale state as
            //    trusted while the host cannot actually compute a current value.
            //
            // The refreshed read answers with the chain's real current state: fresh and
            // verified when it healed, honestly unverifiable for a live torn spell, or -- when
            // the fault persists uncaught -- a FAULTED pull, which the wire contract already
            // defines (the transport decides retry policy; the mirror is left unchanged).
            //
            // A chain an earlier read already resolved to clean-serving-last-good is not
            // detectable here (the park launders on the second scan) -- but it degrades
            // SAFELY: the payload's stamp claims the old triggers it genuinely reflects, so
            // mirrors and the barrier converge on that honest older snapshot, and freshness
            // returns with the next value-changing publication.
            await RefreshUnverifiableChainAsync(node);
            await node.ReadWithEvidence();
        }
        var (value, evidence, sequence) = node.ValueEvidenceAndSequence;
        return new ValuePayload<T>(node.Id, node.Context.Epoch, sequence, value, evidence.Stamp.Serialize(), evidence.Unverifiable);
    }

    // Dirty every source whose current evidence is unverifiable (recursively, sources before
    // consumers) so one Get re-evaluates the whole poisoned chain bottom-up: refreshing only
    // this node would just re-consume the sticky no-claim evidence and republish it. The whole
    // chain is invalidated under one exclusive ContextLock acquisition -- the same discipline
    // as MemoBase.Invalidate() and a parent-driven Stale, so no evaluation's commit window
    // interleaves with a half-invalidated chain.
    private static async Task RefreshUnverifiableChainAsync(SignalHandlR handle)
    {
        var scope = handle.Context.ReactionScope;
        try
        {
            using (await scope.ContextLock.ExclusiveLockAsync())
            {
                await InvalidateUnverifiableChainAsync(handle);
            }
        }
        finally
        {
            GC.KeepAlive(scope);
        }
    }

    private static async Task InvalidateUnverifiableChainAsync(SignalHandlR handle)
    {
        foreach (var source in handle.Sources)
        {
            // Recurse into unverifiable sources AND non-clean ones: a dependency whose
            // evaluation FAULTED keeps its previous verified evidence and parks at CacheCheck
            // (the core's serve-last-good contract), so filtering on unverifiability alone
            // would let the refreshed consumer clean-commit that stale cached value as
            // VERIFIED -- and lose the unverifiability marker that makes pulls refresh at all.
            // Forcing the parked part of the chain to genuinely re-evaluate answers honestly:
            // still unverifiable while the fault persists, freshly verified once it healed.
            // Signals never leave CacheClean and are never recursed; re-evaluating a merely
            // scan-pending node is an equal-value commit that dirties nobody.
            if (source is SignalHandlR sourceHandle
                && (sourceHandle.Evidence.Unverifiable || sourceHandle.stateCell.State != CacheState.CacheClean))
            {
                await InvalidateUnverifiableChainAsync(sourceHandle);
            }
        }
        await handle.InvalidateAndPropagateAsync(CacheState.CacheDirty);
    }

    /// <summary>
    /// Advertise the node's current publication (id + ordering header + stamp, no value).
    /// Called by the export reaction on every value change and by the heartbeat; also callable
    /// directly, e.g. when a consumer (re)subscribes.
    /// </summary>
    public void NotifyStale()
    {
        var (_, evidence, sequence) = node.ValueEvidenceAndSequence;
        var notification = new StaleNotification(node.Id, node.Context.Epoch, sequence, evidence.Stamp.Serialize());
        // The notification is snapshotted above on the caller's flow (the same publication the
        // export reaction observed); only the SEND is detached. Publishing is bridge work, not
        // reaction work: the export reaction calls this while its flow holds the context lock
        // in upgradeable mode, and an in-process bridge that synchronously feeds a same-context
        // signal (an outbox, a local mirror) would inherit that scope and be refused as a
        // recursive exclusive acquisition -- the advertisement silently lost to
        // LastPublishError until the next value change or heartbeat.
        DetachedFlow.Run(() => publishStale(notification), ex => lastPublishError = ex);
    }

    /// <summary>
    /// Re-advertise the current publication every <paramref name="period"/>: the liveness
    /// mechanism for everything the value-change cascade deliberately does not push --
    /// evidence-only transitions, and notifications lost by an unreliable transport.
    /// </summary>
    public void StartHeartbeat(TimeSpan period, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        if (heartbeat != null)
        {
            throw new InvalidOperationException("The heartbeat is already running.");
        }

        var provider = timeProvider ?? TimeProvider.System;
        heartbeat = provider.CreateTimer(_ => NotifyStale(), null, period, period);
    }

    public void Dispose()
    {
        heartbeat?.Dispose();
        heartbeat = null;
        exportReaction?.Dispose();
        exportReaction = null;
    }
}
