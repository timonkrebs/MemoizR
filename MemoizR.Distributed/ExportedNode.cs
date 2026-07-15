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
        var (value, evidence, sequence) = node.ValueEvidenceAndSequence;
        if (evidence.Unverifiable)
        {
            // An unverifiable publication can be STICKY while the node is lazily clean:
            // contagion from a since-healed source survives an equal-value parent scan (the
            // scan proves the inputs did not change, not that the cached value was ever
            // vouched for), so the node would answer every heartbeat-driven pull with the same
            // no-claim publication and sequence -- which the consumer's mirror drops as a
            // duplicate, leaving it unverifiable forever. A pull is the express request for
            // the host's current best claim: force ONE fresh evaluation of the unverifiable
            // chain through the ordinary invalidation entry (exactly what an upstream write
            // would do) and answer with its outcome -- verified now, or honestly still
            // unverifiable (a live torn spell; no retry loop, the next heartbeat tries again).
            await RefreshUnverifiableChainAsync(node);
            await node.ReadWithEvidence();
            (value, evidence, sequence) = node.ValueEvidenceAndSequence;
        }
        return new ValuePayload<T>(node.Id, node.Context.Epoch, sequence, value, evidence.Stamp.Serialize(), evidence.Unverifiable);
    }

    // Dirty every source whose current evidence is unverifiable (recursively, sources before
    // consumers) so one Get re-evaluates the whole poisoned chain bottom-up: refreshing only
    // this node would just re-consume the sticky no-claim evidence and republish it.
    private static async Task RefreshUnverifiableChainAsync(SignalHandlR handle)
    {
        foreach (var source in handle.Sources)
        {
            if (source is SignalHandlR sourceHandle && sourceHandle.Evidence.Unverifiable)
            {
                await RefreshUnverifiableChainAsync(sourceHandle);
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
        DetachedFlow.Run(() => PublishSafelyAsync(notification));
    }

    private async Task PublishSafelyAsync(StaleNotification notification)
    {
        try
        {
            await publishStale(notification);
        }
        catch (Exception ex)
        {
            lastPublishError = ex;
        }
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
