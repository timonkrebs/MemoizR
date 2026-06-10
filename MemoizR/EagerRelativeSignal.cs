namespace MemoizR;

public sealed class EagerRelativeSignal<T> : MemoHandlR<T>, IStateGetR<T>
{
    private Lock Lock { get; } = new();

    internal EagerRelativeSignal(T value, Context context) : base(context)
    {
        SetValueAndStamp(value, CausalityStamp.ForSignal(Id, 0));
    }

    public async Task Set(Func<T, T> fn)
    {
        // Resolve once + strong root: see Signal.Set.
        var scope = Context.ReactionScope;
        try
        {
            // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
            using (await mutex.LockAsync())
            using (await scope.ContextLock.ExclusiveLockAsync())
            {
                // only updating the value should be locked
                lock (Lock)
                {
                    // Every Set bumps the trigger: a relative update always propagates CacheDirty
                    // (there is no equality short-cut here), so the trigger mirrors exactly what
                    // observers are told. Same atomic read-modify-write-under-Lock as Signal.Set.
                    Stamp.TryGetTrigger(Id, out var trigger);
                    SetValueAndStamp(fn(Value), CausalityStamp.ForSignal(Id, trigger + 1));
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

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        try
        {
            using (await mutex.LockAsync())
            using (await scope.ContextLock.UpgradeableLockAsync())
            {
                Context.CheckDependenciesTheSame(this);

                // One box read: the stamp recorded for the capturing evaluation must describe
                // exactly the value returned (a concurrent Set must not split the pair).
                var (value, stamp) = ValueAndStamp;
                Context.RecordSourceStamp(scope.CurrentReaction, Id, stamp);
                return value;
            }
        }
        finally
        {
            GC.KeepAlive(scope); // strong root: the lock identity must outlive the tracked read
        }
    }
}
