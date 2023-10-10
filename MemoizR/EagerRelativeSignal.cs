namespace MemoizR;

public sealed class EagerRelativeSignal<T> : MemoHandlR<T>
{
    internal EagerRelativeSignal(T value, Context context, string label = "Label") : base(context, null)
    {
        this.value = value;
        this.label = label;
    }

    public async Task Set(Func<T?, T> fn)
    {
        // The naming of the lock could be confusing because Set must be locked by ReadLock.
        // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
        // Must be Upgradeable because it could change to "Writeble-Lock" if something synchronously reactive is listening.
        using (await context.contextLock.ExclusiveLockAsync())
        {
            // only updating the value should be locked
            lock (this)
            {
                Thread.MemoryBarrier();
                value = fn(value);
                Thread.MemoryBarrier();
            }

            for (int i = 0; i < Observers.Length; i++)
            {
                var observer = Observers[i];
                await observer.Stale(CacheState.CacheDirty);
            }
        }
    }

    public async Task<T?> Get()
    {
        if (context.CurrentReaction == null)
        {
            Thread.MemoryBarrier();
            return value;
        }

        // The naming of the lock could be confusing because Set must be locked by WriteLock.
        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await context.contextLock.UpgradeableLockAsync())
        {
            context.CheckDependenciesTheSame(this);
        }

        Thread.MemoryBarrier();
        return value;
    }
}
