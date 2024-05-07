namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>, IStateGetR<T?>
{
    internal Signal(T value, Context context) : base(context)
    {
        Value = value;
    }

    public async Task Set(T value)
    {
        // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
        // Must be Upgradeable because it could change to "Writeble-Lock" if something synchronously reactive is listening.
        using (await Context.ContextLock.ExclusiveLockAsync())
        {
            if (Equals(Value, value))
            {
                for (int i = 0; i < Observers.Length; i++)
                {
                    if (Observers[i].TryGetTarget(out var o))
                    {
                        await o.Stale(CacheState.CacheCheck);
                    }
                }
                return;
            }

            // only updating the value should be locked
            lock (this)
            {
                Value = value;
            }

            for (int i = 0; i < Observers.Length; i++)
            {
                if (Observers[i].TryGetTarget(out var o))
                {
                    await o.Stale(CacheState.CacheDirty);
                }
            }
        }
    }

    public async Task<T?> Get()
    {
        Context.CreateNewScopeIfNeeded();
        if (Context.ReactionScope.CurrentReaction == null)
        {
            return Value;
        }

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await Context.ContextLock.UpgradeableLockAsync())
        {
            Context.CheckDependenciesTheSame(this);
        }

        return Value;
    }
}
