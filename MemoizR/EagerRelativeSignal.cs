namespace MemoizR;

public sealed class EagerRelativeSignal<T> : MemoHandlR<T>, IStateGetR<T>
{
    internal EagerRelativeSignal(T value, Context context) : base(context)
    {
        this.Value = value;
    }

    public async Task Set(Func<T, T> fn)
    {
        // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
        // Must be Upgradeable because it could change to "Writeble-Lock" if something synchronously reactive is listening.
        using (await mutex.LockAsync())
        using (await Context.ContextLock.ExclusiveLockAsync())
        {
            // only updating the value should be locked
            lock (this)
            {
                Value = fn(Value);   
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
    
    public async Task<T> Get()
    {
        if (Context.CurrentReaction == null)
        {
            return Value;
        }

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await mutex.LockAsync())
        using (await Context.ContextLock.UpgradeableLockAsync())
        {
            Context.CheckDependenciesTheSame(this);
        }

        return Value;
    }
}
