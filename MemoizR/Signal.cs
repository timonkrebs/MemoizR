namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>, IStateGetR<T?>
{
    private Lock Lock { get; } = new();
    internal Signal(T value, Context context) : base(context)
    {
        Value = value;
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
                    Value = value;
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
        // An unpinned flow can have no capturing reaction (its scope would be freshly minted),
        // so the read needs no scope at all.
        if (!Context.HasFlowScope)
        {
            return Value;
        }

        var scope = Context.GetOrCreateScope();
        if (scope.CurrentReaction == null)
        {
            return Value;
        }

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await scope.ContextLock.UpgradeableLockAsync())
        {
            Context.CheckDependenciesTheSame(this);
        }
        GC.KeepAlive(scope); // strong root: the lock identity must outlive the tracked read

        return Value;
    }
}
