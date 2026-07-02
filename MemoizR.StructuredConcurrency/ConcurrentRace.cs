namespace MemoizR.StructuredConcurrency;

// ConcurrentRace deliberately deviates from the MemoBase<T> protocol: it recomputes on EVERY
// Get (there is no Clean fast path serving a cached value), so the CacheStateCell generation
// guard buys it nothing -- a Stale clobbered by its State=CacheClean can never cause a stale
// READ, because the next Get recomputes regardless. For the same reason its Stale escalates
// observers straight to CacheDirty: a race result is non-memoized, so observers cannot verify
// "did it really change?" via a cheap re-check and must recompute. The inherited stateCell is
// intentionally unused; State here is only a cycle-detection marker.
public sealed class ConcurrentRace<T, I> : MemoHandlR<T>, IMemoizR, IStampedGetR<T>
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
    // shared one. Update returns the freshly published (value, stamp) pair, so both entry points
    // thread it straight through.
    public async Task<T> Get()
    {
        return (await GetWithStamp()).Value;
    }

    public Task<(T Value, CausalityStamp Stamp)> GetWithStamp()
    {
        ActorFlowGuards.RejectLockNodeReadInsideActorComputation();
        return Context.EvaluateUnderLockAsync(mutex, Update);
    }

    /** run the computation fn, updating the cached value */
    private async Task<(T Value, CausalityStamp Stamp)> Update()
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

        // Tracked reads by the racing branches (the parent-flow action plus the child tasks,
        // which inherit this scope) record the source stamps they observed to this node's
        // bucket. The bucket is closed the MOMENT a winner is selected -- Run still awaits the
        // losers, so reads they perform after that point would otherwise land in the capture
        // and widen the published stamp with versions the winning value never consumed. The
        // CompareExchange latch keeps the first close when a slower sibling also completes.
        Context.BeginStampCapture(this);
        StampCapture? winnerCapture = null;

        try
        {
            State = CacheState.Evaluating;
            using var job = new StructuredRaceJob<T, I>(action, fns, Context.CancellationTokenSource!)
            {
                OnWinnerSelected = () => Interlocked.CompareExchange(ref winnerCapture, Context.TakeStampCapture(this), null),
            };
            var newValue = await job.Run(Context.CancellationTokenSource!.Token);
            PublishValueWithStamps(newValue, winnerCapture ?? Context.TakeStampCapture(this));
            State = CacheState.CacheClean;
        }
        catch
        {
            State = CacheState.CacheDirty;
            throw;
        }
        finally
        {
            // Drop a capture left open by the failure paths; a no-op after a successful publish.
            Context.TakeStampCapture(this);
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

        // The node mutex is still held: nothing can republish between the update above and this
        // box read, so the pair is the one this update produced.
        return ValueAndStamp;
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
