namespace MemoizR;

// Shared skeleton for every cached, recomputing node (MemoizR<T>, ConcurrentMap<T>,
// ConcurrentMapReduce<T>): the lock-free Clean fast path, the locked Get slow path, the
// CacheCheck parent scan, and the generation-guarded recompute protocol (see
// docs/architecture/concurrency.md §4-§7 and CacheStateCell). Subclasses supply only the
// computation (ComputeAsync) and two small policy hooks. The protocol previously lived as three
// byte-identical copies, which had to be edited in lockstep for every hardening; keeping it here
// means a node type CANNOT wire the lost-update guard subtly differently.
//
// Deliberately NOT on this base: ReactionBase (push-driven, no Get, debounce-scheduled commits)
// and ConcurrentRace (uncached -- it recomputes on every Get, so the guard buys it nothing).
public abstract class MemoBase<T> : MemoHandlR<T>, IMemoizR, IStateGetR<T>
{
    // State is read on the lock-free Get fast path (alongside the volatile CurrentReaction); the
    // inherited cell exposes a lock-free volatile read and guards transitions so a concurrent
    // Stale can't be clobbered by an in-flight recompute committing Clean (see CacheStateCell).
    private protected CacheState State => stateCell.State;

    // The only external writer of this is the diamond down-link (a parent marking us dirty after
    // it recomputed), which must be absorbed -- not generation-bumped -- during our own evaluation.
    CacheState IMemoizR.State { get => stateCell.State; set => stateCell.InvalidateFromParent(value); }

    internal MemoBase(Context context) : base(context)
    {
        stateCell.Force(CacheState.CacheDirty);
    }

    // Run this node's computation and return the new value. Called inside the evaluation window:
    // dependency capture is installed on the flow's scope and the cell is marked Evaluating.
    internal abstract Task<T> ComputeAsync();

    // Whether this node rewires its own source/observer links after computing. ConcurrentMap
    // returns false: its StructuredResultsJob children capture and wire the links themselves.
    internal virtual bool RewireOwnLinks => true;

    // Value comparison for the diamond down-link guard (observers are only dirtied when the
    // recomputed value actually changed). ConcurrentMap overrides with a sequence comparison.
    internal virtual bool ValuesEqual(T oldValue, T newValue) => Equals(oldValue, newValue);

    public async Task<T> Get()
    {
        // An UNPINNED flow's scope would be freshly minted with CurrentReaction == null by
        // construction, so a clean read from one needs no scope at all: two volatile reads and
        // out -- no lock, no allocation. (Without this, every top-level Get minted and
        // registered a scope just to look at a field that is always null.)
        if (State == CacheState.CacheClean && !Context.HasFlowScope)
        {
            return Value;
        }

        var scope = Context.GetOrCreateScope();
        if (State == CacheState.CacheClean && scope.CurrentReaction == null)
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
                Context.EnterEvaluationScope();
                try
                {
                    if (scope.CurrentReaction != null)
                    {
                        Context.CheckDependenciesTheSame(this);
                    }

                    // if someone else did read the graph while this thread was blocked it could be that this is already Clean
                    if (State == CacheState.CacheClean)
                    {
                        return Value;
                    }

                    await UpdateIfNecessary();
                }
                finally
                {
                    Context.ExitEvaluationScope();
                }
            }
        }
        finally
        {
            // Pin the weakly-registered scope for the whole evaluation: were it collected
            // mid-flight, later Context.ReactionScope resolutions would resurrect a DIFFERENT
            // instance whose fresh ContextLock is not the one this evaluation holds.
            GC.KeepAlive(scope);
        }

        return Value;
    }

    /** update() if dirty, or a parent turns out to be dirty. */
    internal async Task UpdateIfNecessary()
    {
        if (State == CacheState.CacheClean)
        {
            return;
        }

        // Snapshot the generation up front: if a Stale invalidates us while we check parents or
        // recompute, the final commit below must not mark us Clean over it.
        var token = stateCell.Generation;

        // If we are potentially dirty, see if we have a parent who has actually changed value
        var parentFaulted = false;
        if (State == CacheState.CacheCheck)
        {
            foreach (var source in Sources)
            {
                if (source is IMemoizR memoizR)
                {
                    var update = memoizR.UpdateIfNecessary(); // updateIfNecessary() can change state
                    await update.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    parentFaulted |= update.IsFaulted;
                }

                if (State == CacheState.CacheDirty)
                {
                    // Stop the loop here so we won't trigger updates on other parents unnecessarily
                    // If our computation changes to no longer use some Sources, we don't
                    // want to update() a source we used last time, but now don't use.
                    break;
                }
            }
        }

        // If we were already dirty or marked dirty by the step above, update.
        if (State == CacheState.CacheDirty)
        {
            await Update();
        }

        if (State == CacheState.Evaluating) throw new InvalidOperationException("Cyclic behavior detected");

        // A parent that faulted never resolved our CacheCheck: committing Clean over it would
        // stop all future re-checks (this Get serves the last good value, but nothing would ever
        // re-dirty us, so the failed parent would never be retried). Stay CacheCheck so every
        // later Get re-attempts the parent until it recovers.
        if (parentFaulted)
        {
            return;
        }

        // By now, we're clean -- unless a Stale invalidated us along the way, in which case leave
        // the node dirty so the next Get recomputes instead of caching a stale value (and
        // re-notify observers that may have committed Clean against our pre-invalidation value).
        await CommitCleanOrRenotifyAsync(token);
    }

    // Whether any evaluation ever committed a value. Only touched under the node mutex.
    private bool hasComputedOnce;

    /** run the computation, updating the cached value */
    private async Task Update()
    {
        var oldValue = Value;

        /* Evaluate the reactive function body, dynamically capturing any other reactives used.
           Resolve the scope once -- it is stable for the whole evaluation (same flow), and the
           local additionally keeps the weakly-held scope strongly referenced until restored. */
        var scope = Context.ReactionScope;
        var prevReaction = scope.CurrentReaction;
        var prevGets = scope.CurrentGets;
        var prevIndex = scope.CurrentGetsIndex;

        scope.CurrentReaction = this;
        scope.CurrentGets = [];
        scope.CurrentGetsIndex = 0;

        // Mark Evaluating and snapshot the generation; commit Clean below only if no Stale
        // invalidates us while the computation runs.
        var token = stateCell.BeginEvaluation();
        try
        {
            Value = await ComputeAsync();
            hasComputedOnce = true;
            // Publish Clean early so lock-free fast-path readers can serve the new value while
            // the links are rewired; the trailing commit below re-confirms after the diamond
            // propagation.
            stateCell.TryCommitClean(token);

            if (RewireOwnLinks)
            {
                UpdateSourceAndObserverLinks();
            }
        }
        catch
        {
            // With a last good value, stay CacheCheck: the documented contract is "serve the
            // last good value; the next write recomputes and recovers" (see
            // Memo_TransientException_DoesNotPoisonNode). Before ANY success there is no good
            // value -- a CacheCheck would let the next Get commit Clean over default(T) and
            // serve fabricated data forever -- so stay Dirty: every Get retries the computation
            // and surfaces the error until one succeeds.
            stateCell.Force(hasComputedOnce ? CacheState.CacheCheck : CacheState.CacheDirty);

            // Unsubscribe the eager capture-time down-links this failed run added for sources
            // that are not among our kept dependencies: the links are never rewired (links keep
            // the PREVIOUS run's set), so without this they would dirty us forever for reads the
            // last good value never depended on.
            var self = (IMemoizR)this;
            foreach (var got in scope.CurrentGets)
            {
                if (!Sources.Contains(got))
                {
                    got.RemoveObserver(self);
                }
            }
            throw;
        }
        finally
        {
            scope.CurrentGets = prevGets;
            scope.CurrentReaction = prevReaction;
            scope.CurrentGetsIndex = prevIndex;
        }

        // handles diamond dependencies if we're the parent of a diamond. Iterating an empty
        // Observers array is a no-op, but ValuesEqual itself can be costly (sequence comparisons),
        // so skip it when nobody is listening.
        if (Observers.Length > 0 && !ValuesEqual(oldValue, Value))
        {
            MarkObserversDirty();
        }

        // We've rerun with the latest values from all of our Sources, so we no longer need to
        // update until a signal changes -- unless a Stale invalidated us mid-evaluation, in which
        // case the commit is dropped, the node stays dirty for the next Get, and observers that
        // raced us to Clean are re-notified.
        await CommitCleanOrRenotifyAsync(token);
    }

    // The IMemoizR fan-in (a parent scanning this node, or a reaction flow where no root Get holds
    // the evaluation scope) reaches the same locked recompute as Get, via the shared scaffold.
    Task IMemoizR.UpdateIfNecessary()
    {
        return Context.UpdateUnderLockAsync(mutex, UpdateIfNecessary);
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return InvalidateAndPropagateAsync(state);
    }
}
