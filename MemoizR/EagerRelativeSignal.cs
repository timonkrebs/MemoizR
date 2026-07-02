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
        // PINNED scope (not the throwaway the plain getter would mint on an unpinned flow), and
        // strongly rooted for the write: this Set runs USER code (fn) under the exclusive lock,
        // and that code may legitimately call AssertEvaluationIsolated -- which resolves the
        // flow's scope and asks whether ITS lock is held. On a throwaway scope the callback
        // would resolve a different instance and read as not isolated; pinning makes the
        // callback see the very scope whose lock is held. (The pin is an AsyncLocal write, but
        // acquiring the exclusive lock below already pays AsyncLocal writes for its own
        // reentrancy scope; plain Signal.Set runs no user code and keeps the cheaper getter.)
        var scope = Context.GetOrCreateScope();
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

    // Tracked read shared with Signal.Get via MemoHandlR.TrackDependency.
    public async Task<T> Get()
    {
        await TrackDependency();
        return Value;
    }
}
