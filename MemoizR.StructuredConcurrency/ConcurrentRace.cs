namespace MemoizR.StructuredConcurrency;

// ConcurrentRace deliberately deviates from the MemoBase<T> protocol: it recomputes on EVERY
// Get (there is no Clean fast path serving a cached value), so the CacheStateCell generation
// guard buys it nothing -- a Stale clobbered by its State=CacheClean can never cause a stale
// READ, because the next Get recomputes regardless. For the same reason its Stale escalates
// observers straight to CacheDirty: a race result is non-memoized, so observers cannot verify
// "did it really change?" via a cheap re-check and must recompute. The inherited stateCell is
// intentionally unused; State here is only a cycle-detection marker.
public sealed class ConcurrentRace<T, I> : MemoHandlR<T>, IMemoizR, IStateGetR<T>
{
    private CacheState State { get; set; } = CacheState.CacheDirty;
    private readonly Func<Task<I>> action;
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, I, Task<T>>> fns;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal ConcurrentRace(
        Func<Task<I>> action,
        IReadOnlyCollection<Func<IStructuredResourceGroup, I, Task<T>>> fns,
        Context context) : base(context)
    {
        this.action = action;
        this.fns = fns;
    }

    public void Cancel()
    {
        Context.CancellationTokenSource?.Cancel();
    }

    // A race recomputes on every Get (no clean fast path); the locked evaluation scaffold is the
    // shared one. Update returns the value, so the generic overload threads it straight through.
    public Task<T> Get()
    {
        return Context.EvaluateUnderLockAsync(mutex, Update);
    }

    /** run the computation fn, updating the cached value */
    private async Task<T> Update()
    {
        if (State == CacheState.Evaluating) throw new InvalidOperationException("Cyclic behavior detected");
        var oldValue = Value;

        /* Evaluate the reactive function body, dynamically capturing any other reactives used */
        var scope = Context.ReactionScope;
        var prevReaction = scope.CurrentReaction;
        var prevGets = scope.CurrentGets;
        var prevIndex = scope.CurrentGetsIndex;

        scope.CurrentReaction = this;
        scope.CurrentGets = [];
        scope.CurrentGetsIndex = 0;
        var prevAmbientContext = LockEngineFlow.EvaluatingContext.Value;
        LockEngineFlow.EvaluatingContext.Value = Context;

        try
        {
            State = CacheState.Evaluating;
            using var job = new StructuredRaceJob<T, I>(action, fns, Context.CancellationTokenSource!);
            Value = await job.Run(Context.CancellationTokenSource!.Token);
            State = CacheState.CacheClean;
        }
        catch
        {
            State = CacheState.CacheDirty;
            throw;
        }
        finally
        {
            scope.CurrentGets = prevGets;
            scope.CurrentReaction = prevReaction;
            scope.CurrentGetsIndex = prevIndex;
            LockEngineFlow.EvaluatingContext.Value = prevAmbientContext;
        }

        // handles diamond dependencies if we're the parent of a diamond.
        if (!Equals(oldValue, Value))
        {
            MarkObserversDirty();
        }

        return Value;
    }

    // Same scaffold as Get; the recomputed value is awaited for effect and discarded.
    Task IMemoizR.UpdateIfNecessary()
    {
        return Context.EvaluateUnderLockAsync(mutex, Update);
    }

    async Task IMemoizR.Stale(CacheState state)
    {
        if (state <= State)
        {
            return;
        }

        State = state;

        await PropagateStaleToObserversAsync(CacheState.CacheDirty);
    }

    // Deliberately NO finalizer: the CancellationTokenSource is CONTEXT-wide and shared by every
    // evaluation in flight, so a finalizer calling Cancel() would abort unrelated work at an
    // arbitrary GC-determined moment on the finalizer thread.
}
