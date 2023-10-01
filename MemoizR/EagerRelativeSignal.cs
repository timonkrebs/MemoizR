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
        using(await context.contextLock.ReaderLockAsync())
        {
            // only updating the value should be locked
            lock (this)
            {
                value = fn(value);
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
        using( await context.contextLock.WriterLockAsync(context.asyncLocalScope.Value))
        {
            var hasCurrentGets = context.CurrentGets == null || context.CurrentGets.Length == 0;
            var currentSourceEqualsThis = context.CurrentReaction?.Sources?.Length > 0
            && context.CurrentReaction.Sources.Length >= context.CurrentGetsIndex + 1
            && context.CurrentReaction.Sources[context.CurrentGetsIndex] == (this);

            if (hasCurrentGets && currentSourceEqualsThis)
            {
                context.CurrentGetsIndex++;
            }
            else
            {
                if (!context.CurrentGets!.Any()) context.CurrentGets = new[] { this };
                else context.CurrentGets = context.CurrentGets!.Union(new[] { this }).ToArray();
            }
        }

        return value;
    }
}
