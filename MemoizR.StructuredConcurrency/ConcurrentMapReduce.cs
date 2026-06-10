namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentMapReduce<T> : MemoHandlR<T>, IMemoizR, IStateGetR<T>
{
    // Read on the lock-free Get fast path (alongside the volatile CurrentReaction); the inherited
    // cell exposes a lock-free volatile read and guards transitions so a concurrent Stale can't
    // be clobbered by an in-flight recompute committing Clean (see CacheStateCell).
    private CacheState State => stateCell.State;
    private IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly Func<T, T, T?> reduce;

    // The only external writer is the diamond down-link (a parent marking us dirty after it
    // recomputed), which must be absorbed -- not generation-bumped -- during our own evaluation.
    CacheState IMemoizR.State { get => stateCell.State; set => stateCell.InvalidateFromParent(value); }

    internal ConcurrentMapReduce(IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns, Func<T, T, T?> reduce, Context context) : base(context)
    {
        this.fns = fns;
        this.reduce = reduce;
        stateCell.Force(CacheState.CacheDirty);
    }

    public void Cancel()
    {
        Context.CancellationTokenSource?.Cancel();
    }

    public async Task<T> Get()
    {
        Context.CreateNewScopeIfNeeded();
        if (State == CacheState.CacheClean && Context.ReactionScope.CurrentReaction == null)
        {
            return Value;
        }

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await mutex.LockAsync())
        using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
        {
            try
            {
                isStartingComponent = Context.CancellationTokenSource == null;
                Context.CancellationTokenSource ??= new();
                
                if (Context.ReactionScope.CurrentReaction != null)
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
                if (isStartingComponent)
                {
                    Context.CancellationTokenSource = null;
                }
                isStartingComponent = false;
            }
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
        if (State == CacheState.CacheCheck)
        {
            foreach (var source in Sources)
            {
                if (source is IMemoizR memoizR)
                {
                    await memoizR.UpdateIfNecessary().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // updateIfNecessary() can change state
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

        // By now, we're clean -- unless a Stale invalidated us along the way, in which case leave
        // the node dirty so the next Get recomputes instead of caching a stale value (and
        // re-notify observers that may have committed Clean against our pre-invalidation value).
        await CommitCleanOrRenotifyAsync(token);
    }

    /** run the computation fn, updating the cached value */
    private async Task Update()
    {
        var oldValue = Value;

        /* Evaluate the reactive function body, dynamically capturing any other reactives used */
        var prevReaction = Context.ReactionScope.CurrentReaction;
        var prevGets = Context.ReactionScope.CurrentGets;
        var prevIndex = Context.ReactionScope.CurrentGetsIndex;

        Context.ReactionScope.CurrentReaction = this;
        Context.ReactionScope.CurrentGets = [];
        Context.ReactionScope.CurrentGetsIndex = 0;

        // Mark Evaluating and snapshot the generation; commit Clean below only if no Stale
        // invalidates us while the job runs.
        var token = stateCell.BeginEvaluation();
        try
        {
            Value = await new StructuredReduceJob<T>(fns, reduce, Context, this).Run(Context.CancellationTokenSource!.Token);
            stateCell.TryCommitClean(token);

            UpdateSourceAndObserverLinks();
        }
        catch
        {
            stateCell.Force(CacheState.CacheCheck);
            throw;
        }
        finally
        {
            Context.ReactionScope.CurrentGets = prevGets;
            Context.ReactionScope.CurrentReaction = prevReaction;
            Context.ReactionScope.CurrentGetsIndex = prevIndex;
        }

        // handles diamond dependencies if we're the parent of a diamond.
        if (!Equals(oldValue, Value))
        {
            MarkObserversDirty();
        }

        // We've rerun with the latest values from all of our Sources, so we no longer need to
        // update until a signal changes -- unless a Stale invalidated us mid-evaluation, in which
        // case the commit is dropped, the node stays dirty for the next Get, and observers that
        // raced us to Clean are re-notified.
        await CommitCleanOrRenotifyAsync(token);
    }

    async Task IMemoizR.UpdateIfNecessary()
    {
        using (await mutex.LockAsync())
        using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
        {
            await UpdateIfNecessary();
        }
    }

    internal Task Stale(CacheState state)
    {
        return InvalidateAndPropagateAsync(state);
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }

    ~ConcurrentMapReduce()
    {
        Cancel();
    }
}
