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
        return (await ReadWithEvidence()).Value;
    }

    public async Task<(T Value, CausalityStamp Stamp)> GetWithStamp()
    {
        var (value, evidence) = await ReadWithEvidence();
        return (value, evidence.Stamp);
    }

    public Task<(T Value, StampEvidence Evidence)> GetWithEvidence() => ReadWithEvidence();

    internal override Task<(T Value, StampEvidence Evidence)> ReadWithEvidence()
    {
        ActorFlowGuards.RejectLockNodeReadInsideActorComputation();
        return Context.EvaluateUnderLockAsync(mutex, async () =>
        {
            // The capturing computation this read belongs to, resolved BEFORE Update installs
            // the race as CurrentReaction: a race is uncached and registers no dependency edge,
            // but the caller's value still consumed this result, so the caller's evidence must
            // include the race's stamp (keyed by the race's id, like any derived source) or the
            // causality of the signals the race read would silently vanish from the caller.
            // Unverifiable race evidence -- and a faulted race whose fallback a caller may
            // publish -- poisons the caller's capture instead (see MemoBase.ReadWithEvidence).
            var caller = Context.ReactionScope.CurrentReaction;
            try
            {
                var (value, raceEvidence) = await Update();
                if (raceEvidence.Unverifiable)
                {
                    Context.MarkStampCaptureUnverifiable(caller);
                }
                else
                {
                    Context.RecordSourceStamp(caller, Id, raceEvidence.Stamp);
                }
                return (value, raceEvidence);
            }
            catch
            {
                Context.MarkStampCaptureUnverifiable(caller);
                throw;
            }
        });
    }

    /** run the computation fn, updating the cached value */
    private async Task<(T Value, StampEvidence Evidence)> Update()
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
        // Reset the ambient racing-branch tag for this race's own frame: when THIS race runs
        // inside another race's branch, its shared action must record as branch 0 of THIS
        // capture, not under the enclosing race's branch id. Restored in the finally (an
        // AsyncLocal mutation would otherwise survive a synchronously-completing evaluation).
        var prevBranch = RaceBranchFlow.Current.Value;
        RaceBranchFlow.Current.Value = 0;

        // Tracked reads record the source stamps they observed to this node's BRANCH-AWARE
        // bucket, tagged with the racing branch that performed them (0 = the shared action on
        // this flow). Ordinary nodes evaluated inside a branch open their own, non-branch-aware
        // captures, which ignore the ambient tag. Two cuts keep the published stamp exactly the
        // evidence that fed the winning value:
        //  - the bucket is CLOSED the moment the winner is selected (the job's win latch
        //    invokes the callback exactly once, atomically with claiming the result), dropping
        //    everything a loser reads afterwards, and
        //  - the capture is SEALED to branch 0 + the winning branch, dropping what losers read
        //    BEFORE the selection too -- their reads never fed the returned value. Poison is
        //    per branch, so a losing branch's mixed re-read cannot destroy a clean winner.
        // The WhenAll join inside Run is the barrier that publishes the locals to this flow.
        Context.BeginStampCapture(this, branchAware: true);
        StampCapture? winnerCapture = null;
        var winnerBranch = 0;

        try
        {
            State = CacheState.Evaluating;
            using var job = new StructuredRaceJob<T, I>(action, fns, Context.CancellationTokenSource!)
            {
                OnWinnerSelected = branch =>
                {
                    winnerBranch = branch;
                    winnerCapture = Context.TakeStampCapture(this);
                },
            };
            var newValue = await job.Run(Context.CancellationTokenSource!.Token);
            PublishValueWithStamps(newValue, winnerCapture ?? Context.TakeStampCapture(this), winnerBranch);
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
            RaceBranchFlow.Current.Value = prevBranch;
        }

        // handles diamond dependencies if we're the parent of a diamond.
        if (!Equals(oldValue, Value))
        {
            MarkObserversDirty();
        }

        // The node mutex is still held: nothing can republish between the update above and this
        // box read, so the pair is the one this update produced.
        return ValueAndEvidence;
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
