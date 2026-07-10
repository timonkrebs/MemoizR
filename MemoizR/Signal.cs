namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>, IStampedGetR<T?>
{
    private Lock Lock { get; } = new();

    // The signal's causality trigger (issue #39), mirroring the published stamp exactly.
    // Guarded by Lock; kept as a plain counter so a Set does not have to walk the published
    // stamp's event tree just to recover the value this node itself bumped last.
    private long trigger;

    internal Signal(T value, Context context) : base(context)
    {
        if (context.StampsEnabled)
        {
            SetValueAndStamp(value, CausalityStamp.ForSignal(Id, 0, context.Epoch));
        }
        else
        {
            SetValueUnstamped(value);
        }
    }

    public async Task Set(T value)
    {
        // Resolve the scope once and keep it strongly rooted for the whole write: repeated getter
        // access can resolve different instances (weak registry + resurrection), which would hand
        // the body a ContextLock other than the one held here.
        var scope = Context.ReactionScope;
        try
        {
            // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
            using (await scope.ContextLock.ExclusiveLockAsync())
            {
                // only updating the value should be locked
                lock (Lock)
                {
                    // The equality check lives under the SAME monitor as the trigger bump:
                    // concurrent Sets run under different flows' ContextLocks, so two racing
                    // same-value writes would otherwise both pass a check taken outside it and
                    // double-bump the trigger for one logical change.
                    if (Equals(Value, value))
                    {
                        // The value did not change: nothing derived from this signal can have
                        // become stale, so do not notify. (Propagating CacheCheck here bumped
                        // observer generations and refused their in-flight commits -- under an
                        // equal-value write storm a long recompute could never commit at all.)
                        // The causality trigger counts value CHANGES (issue #39), so it is not
                        // bumped either.
                        return;
                    }

                    // Publish the bumped trigger atomically with the value: the stamp rides in
                    // the same box swap, so readers can never pair them inconsistently. A
                    // stamps-disabled context builds no stamp at all.
                    if (Context.StampsEnabled)
                    {
                        SetValueAndStamp(value, CausalityStamp.ForSignal(Id, ++trigger, Context.Epoch));
                    }
                    else
                    {
                        SetValueUnstamped(value);
                    }
                }

                await PropagateStaleToObserversAsync(CacheState.CacheDirty);
            }
        }
        finally
        {
            GC.KeepAlive(scope);
        }
    }

    // Tracked read shared with EagerRelativeSignal via MemoHandlR.TrackDependencyAndRead; the
    // (T, stamp) projection converts to this signal's nullable (T?, stamp) surface.
    public Task<T?> Get()
    {
        ActorFlowGuards.RejectLockNodeReadInsideActorComputation();

        // An unpinned flow cannot be capturing: hand out the box's cached completed task -- the
        // plain top-level read allocates nothing. Task<T> and Task<T?> are the same runtime type
        // for an unconstrained T (the ? is annotation-only), hence the cast through object.
        if (!Context.HasFlowScope)
        {
            return (Task<T?>)(object)CachedValueTask;
        }
        return GetTracked();
    }

    private async Task<T?> GetTracked()
    {
        return (await TrackDependencyAndRead()).Value;
    }

    public async Task<(T? Value, CausalityStamp Stamp)> GetWithStamp()
    {
        var (value, evidence) = await TrackDependencyAndRead();
        return (value, evidence.Stamp);
    }

    public async Task<(T? Value, StampEvidence Evidence)> GetWithEvidence()
    {
        var (value, evidence) = await TrackDependencyAndRead();
        return (value, evidence);
    }
}
