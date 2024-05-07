namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentMapReduce<T> : MemoHandlR<T>, IMemoizR, IStateGetR<T>
{
    private CacheState State { get; set; } = CacheState.CacheClean;
    private IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns;
    private readonly Func<T, T, T?> reduce;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal ConcurrentMapReduce(IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns, Func<T, T, T?> reduce, Context context) : base(context)
    {
        this.fns = fns;
        this.reduce = reduce;
        this.State = CacheState.CacheDirty;
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
        using (await Context.ContextLock.UpgradeableLockAsync())
        {
            try
            {
                isStartingComponent = Context.CancellationTokenSource == null;
                Context.CancellationTokenSource ??= new CancellationTokenSource();
                
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

        // By now, we're clean
        State = CacheState.CacheClean;
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

        try
        {
            State = CacheState.Evaluating;
            Value = await new StructuredReduceJob<T>(fns, reduce, Context.CancellationTokenSource!).Run();
            State = CacheState.CacheClean;

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
                        ? [new WeakReference<IMemoizR>(this)]
                        : [.. source.Observers, new WeakReference<IMemoizR>(this)];
                }
            }
            else if (Sources.Any() && Context.ReactionScope.CurrentGetsIndex < Sources.Length)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.ReactionScope.CurrentGetsIndex);
                Sources = [.. Sources.Take(Context.ReactionScope.CurrentGetsIndex)];
            }
        }
        catch
        {
            State = CacheState.CacheCheck;
            throw;
        }
        finally
        {
            Context.ReactionScope.CurrentGets = prevGets;
            Context.ReactionScope.CurrentReaction = prevReaction;
            Context.ReactionScope.CurrentGetsIndex = prevIndex;
        }

        // handles diamond dependencies if we're the parent of a diamond.
        if (!Equals(oldValue, Value) && Observers.Length > 0)
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

        // We've rerun with the latest values from all of our Sources.
        // This means that we no longer need to update until a signal changes
        State = CacheState.CacheClean;
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
        using (await Context.ContextLock.UpgradeableLockAsync())
        {
            await UpdateIfNecessary();
        }
    }

    internal async Task Stale(CacheState state)
    {
        if (state <= State)
        {
            return;
        }

        State = state;

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

    ~ConcurrentMapReduce()
    {
        Cancel();
    }
}
