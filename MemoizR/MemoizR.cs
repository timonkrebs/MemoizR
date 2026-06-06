namespace MemoizR;

public sealed class MemoizR<T> : MemoHandlR<T>, IMemoizR, IStateGetR<T>
{
    // State is read on the lock-free Get fast path (alongside the volatile CurrentReaction); the
    // cell exposes a lock-free volatile read and guards transitions so a concurrent Stale can't be
    // clobbered by an in-flight recompute committing Clean (see CacheStateCell).
    private readonly CacheStateCell stateCell = new(CacheState.CacheClean);
    private CacheState State => stateCell.State;
    private Func<CancellationTokenSource, Task<T>> fn;

    // The only external writer of this is the diamond down-link (a parent marking us dirty after
    // it recomputed), which must be absorbed -- not generation-bumped -- during our own evaluation.
    CacheState IMemoizR.State { get => stateCell.State; set => stateCell.InvalidateFromParent(value); }

    internal MemoizR(Func<CancellationTokenSource, Task<T>> fn, Context context) : base(context)
    {
        this.fn = fn;
        stateCell.Force(CacheState.CacheDirty);
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
        // the node dirty so the next Get recomputes instead of caching a stale value.
        stateCell.TryCommitClean(token);
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
        // invalidates us while fn runs.
        var token = stateCell.BeginEvaluation();
        try
        {
            Value = await fn(Context.CancellationTokenSource!);
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
        // case the commit is dropped and the node stays dirty for the next Get.
        stateCell.TryCommitClean(token);
    }

    // Rewire our source up-links and the parents' observer down-links to match the Sources captured
    // during this evaluation (Context.ReactionScope.CurrentGets). Extracted from Update to keep its
    // Cognitive Complexity in budget; only ever runs under the ContextLock-serialized evaluation.
    private void UpdateSourceAndObserverLinks()
    {
        // if the sources have changed, update source & observer links
        if (Context.ReactionScope.CurrentGets.Length > 0)
        {
            // remove all old Sources' .observers links to us
            RemoveParentObservers(Context.ReactionScope.CurrentGetsIndex);
            // update source up links
            if (Sources.Any() && Context.ReactionScope.CurrentGetsIndex > 0)
            {
                Sources = [.. Sources.Take(Context.ReactionScope.CurrentGetsIndex), .. Context.ReactionScope.CurrentGets];
            }
            else
            {
                Sources = Context.ReactionScope.CurrentGets;
            }

            for (var i = Context.ReactionScope.CurrentGetsIndex; i < Sources.Length; i++)
            {
                // Add ourselves to the end of the parent .observers array
                var source = Sources[i];
                source.Observers = !source.Observers.Any()
                    ? [new(this)]
                    : [.. source.Observers, new(this)];
            }
        }
        else if (Sources.Any() && Context.ReactionScope.CurrentGetsIndex < Sources.Length)
        {
            // remove all old Sources' .observers links to us
            RemoveParentObservers(Context.ReactionScope.CurrentGetsIndex);
            Sources = [.. Sources.Take(Context.ReactionScope.CurrentGetsIndex)];
        }
    }

    // Mark our observers dirty so they re-evaluate (the diamond down-link). Iterating an empty
    // Observers array is a no-op, so the caller's value-changed guard is all that's needed.
    private void MarkObserversDirty()
    {
        // We've changed value, so mark our children as dirty so they'll reevaluate
        foreach (var observer in Observers)
        {
            if (observer.TryGetTarget(out var o))
            {
                o.State = CacheState.CacheDirty;
            }
        }
    }

    private void RemoveParentObservers(int index)
    {
        if (!Sources.Any()) return;
        foreach (var source in Sources.Skip(index))
        {
            source.Observers = [.. source.Observers.Where(x => x.TryGetTarget(out var o) ? o != this : false)];
        }
    }

    async Task IMemoizR.UpdateIfNecessary()
    {
        using (await mutex.LockAsync())
        using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
        {
            await UpdateIfNecessary();
        }
    }

    internal async Task Stale(CacheState state)
    {
        // Escalate atomically and bump the generation so an in-flight recompute on another flow
        // cannot commit Clean over this invalidation. No change => already at least this dirty.
        if (!stateCell.Invalidate(state))
        {
            return;
        }

        foreach (var observer in Observers)
        {
            if (observer.TryGetTarget(out var o))
            {
                await o.Stale(CacheState.CacheCheck);
            }
        }
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }
}