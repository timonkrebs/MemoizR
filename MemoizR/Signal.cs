namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>
{
    internal Signal(T value, Context context, string label = "Label", Func<T?, T?, bool>? equals = null) : base(context, equals)
    {
        this.value = value;
        this.label = label;
    }

    public void Set(T value)
    {
        if (equals(this.value, value))
        {
            return;
        }

        // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process
        context.contextLock.EnterUpgradeableReadLock();
        try
        {
            for (int i = 0; i < Observers.Length; i++)
            {
                var observer = Observers[i];
                observer.Stale(CacheState.CacheDirty);
            }

            // only updating the value should be locked
            lock (this)
            {
                this.value = value;
            }
        }
        finally
        {
            context.contextLock.ExitUpgradeableReadLock();
        }
    }

    public T? Get()
    {
        if (context.CurrentReaction == null)
        {
            return value;
        }

        // only one thread should evaluate the graph at a time. <otherwise the context could get messed
        context.contextLock.EnterWriteLock();
        try
        {
            if ((context.CurrentGets == null || !(context.CurrentGets.Length > 0)) &&
              (context.CurrentReaction.sources != null && context.CurrentReaction.sources.Length > 0) &&
              context.CurrentReaction.sources[context.CurrentGetsIndex].Equals(this)
            )
            {
                context.CurrentGetsIndex++;
            }
            else
            {
                if (!context.CurrentGets!.Any()) context.CurrentGets = new[] { this };
                else context.CurrentGets = context.CurrentGets!.Union(new[] { this }).ToArray();
            }
        }
        finally
        {
            context.contextLock.ExitWriteLock();
        }

        return value;
    }
}
