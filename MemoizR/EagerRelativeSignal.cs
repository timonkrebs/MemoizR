namespace MemoizR;

public sealed class EagerRelativeSignal<T> : MemoHandlR<T>, IStampedGetR<T>
{
    private Lock Lock { get; } = new();

    // The signal's causality trigger (issue #39), mirroring the published stamp exactly.
    // Guarded by Lock; a plain counter so Set does not walk the published stamp's event tree.
    private long trigger;

    internal EagerRelativeSignal(T value, Context context) : base(context)
    {
        SetValueAndStamp(value, CausalityStamp.ForSignal(Id, 0, context.Epoch));
    }

    public async Task Set(Func<T, T> fn)
    {
        // PINNED scope (not the throwaway the plain getter would mint on an unpinned flow), and
        // strongly rooted for the write: this Set runs USER code (fn) under the exclusive lock,
        // and that code may legitimately call AssertEvaluationIsolated -- which resolves the
        // flow's scope and asks whether ITS lock is held. On a throwaway scope the callback
        // would resolve a different instance and read as not isolated; pinning makes the
        // callback see the very scope whose lock is held. (The pin is an AsyncLocal write, but
        // acquiring the exclusive lock below already pays AsyncLocal writes for its own
        // reentrancy scope; plain Signal.Set runs no user code and keeps the cheaper getter.)
        var scope = Context.GetOrCreateScope();
        try
        {
            // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
            using (await scope.ContextLock.ExclusiveLockAsync())
            {
                // The relative read-modify-write is serialized by lock (Lock); the per-node mutex
                // adds nothing for a signal (it has no recompute to serialize -- ADR 0002 scopes
                // the mutex to recomputing nodes), so, like Signal.Set, it is not taken.
                lock (Lock)
                {
                    // Every Set bumps the causality trigger: a relative update always propagates
                    // CacheDirty (there is no equality short-cut here), so the trigger mirrors
                    // exactly what observers are told (issue #39).
                    SetValueAndStamp(fn(Value), CausalityStamp.ForSignal(Id, ++trigger, Context.Epoch));
                }

                await PropagateStaleToObserversAsync(CacheState.CacheDirty);
            }
        }
        finally
        {
            GC.KeepAlive(scope);
        }
    }

    // Tracked read shared with Signal via MemoHandlR.TrackDependencyAndRead.
    public Task<T> Get()
    {
        ActorFlowGuards.RejectLockNodeReadInsideActorComputation();

        // An unpinned flow cannot be capturing: hand out the box's cached completed task -- the
        // plain top-level read allocates nothing.
        if (!Context.HasFlowScope)
        {
            return CachedValueTask;
        }
        return GetTracked();
    }

    private async Task<T> GetTracked()
    {
        return (await TrackDependencyAndRead()).Value;
    }

    public async Task<(T Value, CausalityStamp Stamp)> GetWithStamp()
    {
        var (value, evidence) = await TrackDependencyAndRead();
        return (value, evidence.Stamp);
    }

    public Task<(T Value, StampEvidence Evidence)> GetWithEvidence() => ReadWithEvidence();
}
