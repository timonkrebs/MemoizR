namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>
{
    internal Signal(T value, Context context, string label = "Label", Func<T?, T?, bool>? equals = null) : base(context, equals)
    {
        this.Value = value;
        this.Label = label;
    }

    public async Task Set(T value)
    {
        // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
        // Must be Upgradeable because it could change to "Writeble-Lock" if something synchronously reactive is listening.
        using (await Context.ContextLock.ExclusiveLockAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
        {
            if (Equals(this.Value, value))
            {
                for (int i = 0; i < Observers.Length; i++)
                {
                    var observer = Observers[i];
                    await observer.Stale(CacheState.CacheCheck);
                }
                return;
            }


            // only updating the value should be locked
            lock (this)
            {
                Thread.MemoryBarrier();
                this.Value = value;
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
        if (Context.CurrentReaction == null)
        {
            Thread.MemoryBarrier();
            return Value;
        }

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await Context.ContextLock.UpgradeableLockAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Context.CheckDependenciesTheSame(this);
        }

        Thread.MemoryBarrier();
        return Value;
    }
}
