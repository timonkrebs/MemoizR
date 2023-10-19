namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentMap<T> : SignalHandlR, IMemoizR
{
    private CacheState State { get; set; } = CacheState.CacheClean;
    private IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns;
    private readonly CancellationTokenSource cancellationTokenSource;
    private IEnumerable<T?> value = new List<T>();

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal ConcurrentMap(IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns, Context context, CancellationTokenSource cancellationTokenSource, string label = "Label") : base(context)
    {
        if(context.saveMode){
            Task.WaitAll(fns.Select(x => x(CancellationToken.None)).ToArray());
        }
        this.fns = fns;
        this.cancellationTokenSource = cancellationTokenSource;
        this.State = CacheState.CacheDirty;
        this.Label = label;
    }

    public async Task<IEnumerable<T?>> Get()
    {
        if (State == CacheState.CacheClean && Context.CurrentReaction == null)
        {
            return value;
        }

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await Context.ContextLock.UpgradeableLockAsync())
        {
            // if someone else did read the graph while this thread was blocekd it could be that this is already Clean
            if (State == CacheState.CacheClean && Context.CurrentReaction == null)
            {
                return value;
            }

            if (Context.CurrentReaction != null)
            {
                Context.CheckDependenciesTheSame(this);
            }

            await UpdateIfNecessary();
        }

        return value;
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
                    await memoizR.UpdateIfNecessary(); // updateIfNecessary() can change state
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

        // By now, we're clean
        State = CacheState.CacheClean;
    }

    /** run the computation fn, updating the cached value */
    private async Task Update()
    {
        /* Evalute the reactive function body, dynamically capturing any other reactives used */
        var prevReaction = Context.CurrentReaction;
        var prevGets = Context.CurrentGets;
        var prevIndex = Context.CurrentGetsIndex;

        Context.CurrentReaction = this;
        Context.CurrentGets = Array.Empty<IMemoHandlR>();
        Context.CurrentGetsIndex = 0;

        try
        {
            value = await new StructuredResultsJob<T>(fns, cancellationTokenSource).Run();

            // if the sources have changed, update source & observer links
            if (Context.CurrentGets.Length > 0)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.CurrentGetsIndex);
                // update source up links
                if (Sources.Any() && Context.CurrentGetsIndex > 0)
                {
                    Sources = Sources.Take(Context.CurrentGetsIndex).Union(Context.CurrentGets).ToArray();
                }
                else
                {
                    Sources = Context.CurrentGets;
                }

                for (var i = Context.CurrentGetsIndex; i < Sources.Length; i++)
                {
                    // Add ourselves to the end of the parent .observers array
                    var source = Sources[i];
                    source.Observers = !source.Observers.Any() 
                        ? new IMemoizR[] { this } 
                        : source.Observers.Union((new[] { this })).ToArray();
                }
            }
            else if (Sources.Any() && Context.CurrentGetsIndex < Sources.Length)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.CurrentGetsIndex);
                Sources = Sources.Take(Context.CurrentGetsIndex).ToArray();
            }
        }
        finally
        {
            Context.CurrentGets = prevGets;
            Context.CurrentReaction = prevReaction;
            Context.CurrentGetsIndex = prevIndex;
        }

        // handles diamond depenendencies if we're the parent of a diamond.
        if (Observers.Length > 0)
        {
            // We've changed value, so mark our children as dirty so they'll reevaluate
            foreach (var observer in Observers)
            {
                observer.State = CacheState.CacheDirty;
            }
        }

        // We've rerun with the latest values from all of our Sources.
        // This means that we no longer need to update until a signal changes
        State = CacheState.CacheClean;
    }

    private void RemoveParentObservers(int index)
    {
        if (!Sources.Any()) return;
        for (var i = index; i < Sources.Length; i++)
        {
            var source = Sources[i]; // We don't actually delete Sources here because we're replacing the entire array soon
            var swap = Array.FindIndex(source.Observers, v => v.Equals(this));
            source.Observers[swap] = source.Observers[source.Observers!.Length - 1];
            source.Observers = source.Observers.SkipLast(1).ToArray();
        }
    }

    Task IMemoizR.UpdateIfNecessary()
    {
        return UpdateIfNecessary();
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
            await observer.Stale(CacheState.CacheCheck);
        }
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }
}
