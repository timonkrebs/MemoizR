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

    public async Task<T> Get()
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

        // Tracked read: register the dependency under this flow's ContextLock. Like Signal.Get,
        // the per-node mutex is not taken -- CheckDependenciesTheSame is already serialized by
        // Context.Lock, and a signal has no recompute for the mutex to guard (ADR 0002).
        using (await scope.ContextLock.UpgradeableLockAsync())
        {
            Context.CheckDependenciesTheSame(this);
        }
        GC.KeepAlive(scope); // strong root: the lock identity must outlive the tracked read

        return Value;
    }
}
