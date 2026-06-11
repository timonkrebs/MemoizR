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
        // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
        // Must be Upgradeable because it could change to "Writeble-Lock" if something synchronously reactive is listening.
        using (await Context.ReactionScope.ContextLock.ExclusiveLockAsync())
        {
            if (Equals(Value, value))
            {
                // The value did not change, but observers may still be mid-check: tell them to
                // re-verify rather than marking them dirty.
                await PropagateStaleToObserversAsync(CacheState.CacheCheck);
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

        return Value;
    }
}
