using MemoizR.AsyncLock;

namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>
{
    internal Signal(T value, Context context, string label = "Label", Func<T?, T?, bool>? equals = null) : base(context, equals)
    {
        this.value = value;
        this.label = label;
    }

    public async Task Set(T value)
    {
        if (equals(this.value, value))
        {
            for (int i = 0; i < Observers.Length; i++)
            {
                var observer = Observers[i];
                await observer.Stale(CacheState.CacheCheck);
            }
            return;
        }

        // The naming of the lock could be confusing because Set must be locked by ReadLock.
        // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
        // Must be Upgradeable because it could change to "Writeble-Lock" if something synchronously reactive is listening.
        using (await context.contextLock.ExclusiveLockAsync())
        {
            // only updating the value should be locked
            lock (this)
            {
                this.value = value;
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
            return value;
        }

        // The naming of the lock could be confusing because Set must be locked by WriteLock.
        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await context.contextLock.UpgradeableLockAsync())
        {
            context.CheckDependenciesTheSame(this);
        }

        return value;
    }
}
