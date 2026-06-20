namespace MemoizR;

public sealed class EagerRelativeSignal<T> : MemoHandlR<T>, IStateGetR<T>
{
    private Lock Lock { get; } = new();

    internal EagerRelativeSignal(T value, Context context) : base(context)
    {
        this.Value = value;
    }

    public async Task Set(Func<T, T> fn)
    {
        // Resolve once + strong root: see Signal.Set.
        var scope = Context.ReactionScope;
        try
        {
            // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
            using (await scope.ContextLock.ExclusiveLockAsync())
            {
                // The relative read-modify-write is serialized by lock (Lock); the per-node mutex
                // adds nothing for a signal (it has no recompute to serialize -- ADR 0002 scopes
                // the mutex to recomputing nodes), so, like Signal.Set, it is not taken.
                lock (Lock)
                {
                    Value = fn(Value);
                }

                await PropagateStaleToObserversAsync(CacheState.CacheDirty);
            }
        }
        finally
        {
            GC.KeepAlive(scope);
        }
    }

    // Identical tracked read to Signal.Get -- shared on the MemoHandlR base.
    public Task<T> Get() => ReadTracked();
}
