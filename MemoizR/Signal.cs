namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>, IStampedGetR<T?>
{
    private Lock Lock { get; } = new();
    internal Signal(T value, Context context) : base(context)
    {
        SetValueAndStamp(value, CausalityStamp.ForSignal(Id, 0, context.Epoch));
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
                if (Equals(Value, value))
                {
                    // The value did not change: nothing derived from this signal can have become
                    // stale, so do not notify. (Propagating CacheCheck here bumped observer
                    // generations and refused their in-flight commits -- under an equal-value
                    // write storm a long recompute could never commit at all.)
                    return;
                }

                // only updating the value should be locked
                lock (Lock)
                {
                    // Read the current trigger and publish (value, trigger + 1) under the same
                    // monitor: concurrent Sets run under different flows' ContextLocks, so this Lock
                    // is what makes the trigger read-modify-write atomic. The bumped stamp rides in
                    // the same box swap as the value, so readers can never pair them inconsistently.
                    Stamp.TryGetTrigger(Id, out var trigger);
                    SetValueAndStamp(value, CausalityStamp.ForSignal(Id, trigger + 1, Context.Epoch));
                }

                await PropagateStaleToObserversAsync(CacheState.CacheDirty);
            }
        }
        finally
        {
            GC.KeepAlive(scope);
        }
    }

    public async Task<T?> Get()
    {
        return (await GetWithStamp()).Value;
    }

    public async Task<(T? Value, CausalityStamp Stamp)> GetWithStamp()
    {
        // An unpinned flow can have no capturing reaction (its scope would be freshly minted),
        // so the read needs no scope at all.
        if (!Context.HasFlowScope)
        {
            return ValueAndStamp;
        }

        var scope = Context.GetOrCreateScope();
        if (scope.CurrentReaction == null)
        {
            return ValueAndStamp;
        }

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        try
        {
            using (await scope.ContextLock.UpgradeableLockAsync())
            {
                Context.CheckDependenciesTheSame(this);

                // One box read: the stamp recorded for the capturing evaluation -- and the one
                // returned to the caller -- must describe exactly the value returned (a concurrent
                // Set must not split the pair).
                var (value, stamp) = ValueAndStamp;
                Context.RecordSourceStamp(scope.CurrentReaction, Id, stamp);
                return (value, stamp);
            }
        }
        finally
        {
            GC.KeepAlive(scope); // strong root: the lock identity must outlive the tracked read
        }
    }
}
