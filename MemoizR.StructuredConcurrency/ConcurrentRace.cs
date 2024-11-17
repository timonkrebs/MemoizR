namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentRace<T, I> : MemoHandlR<T>, IMemoizR, IStateGetR<T>
{
    private CacheState State { get; set; } = CacheState.CacheDirty;
    Func<Task<I>> action;
    private IReadOnlyCollection<Func<CancellationTokenSource, I, Task<T>>> fns;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal ConcurrentRace(
        Func<Task<I>> action,
        IReadOnlyCollection<Func<CancellationTokenSource, I, Task<T>>> fns,
        Context context) : base(context)
    {
        this.action = action;
        this.fns = fns;
    }

    public void Cancel()
    {
        Context.CancellationTokenSource?.Cancel();
    }

    public async Task<T> Get()
    {
        Context.CreateNewScopeIfNeeded();
        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await mutex.LockAsync())
        using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
        {
            try
            {
                isStartingComponent = Context.CancellationTokenSource == null;
                Context.CancellationTokenSource ??= new();
                return await Update();
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
    }

    /** run the computation fn, updating the cached value */
    private async Task<T> Update()
    {
        if (State == CacheState.Evaluating) throw new InvalidOperationException("Cyclic behavior detected");
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
            Value = await new StructuredRaceJob<T, I>(action, fns, Context.CancellationTokenSource!).Run();
            State = CacheState.CacheClean;

            // if the sources have changed, update source & observer links
            if (Context.ReactionScope.CurrentGets.Length > 0)
            {
                for (var i = Context.ReactionScope.CurrentGetsIndex; i < Sources.Length; i++)
                {
                    // Add ourselves to the end of the parent .observers array
                    var source = Sources[i];
                    source.Observers = !source.Observers.Any()
                        ? [this]
                        : [.. source.Observers, this];
                }
            }
        }
        catch
        {
            State = CacheState.CacheDirty;
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
            await Task.WhenAll(Observers.Select(o => o.Stale(CacheState.CacheDirty)));
        }

        return Value;
    }

    async Task IMemoizR.UpdateIfNecessary()
    {
        using (await mutex.LockAsync())
        using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
        {
            await Update();
        }
    }

    internal async Task Stale(CacheState state)
    {
        if (state <= State)
        {
            return;
        }

        State = state;

        await Task.WhenAll(Observers.Select(o => o.Stale(CacheState.CacheDirty)));
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }

    ~ConcurrentRace()
    {
        Cancel();
    }
}
