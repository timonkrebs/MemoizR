namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentMapReduce<T> : SignalHandlR, IMemoizR
{
    private CacheState State { get; set; } = CacheState.CacheClean;
    private IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns;
    private readonly Func<T, T, T?> reduce;
    private readonly CancellationTokenSource cancellationTokenSource;
    private T? value = default;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal ConcurrentMapReduce(IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns, Func<T, T, T?> reduce, Context context, CancellationTokenSource cancellationTokenSource, string label = "Label") : base(context)
    {
        this.fns = fns;
        this.reduce = reduce;
        this.cancellationTokenSource = cancellationTokenSource;
        this.State = CacheState.CacheDirty;
        this.label = label;
    }

    public async Task<T?> Get()
    {
        if (State == CacheState.CacheClean && context.CurrentReaction == null)
        {
            Thread.MemoryBarrier();
            return value;
        }

        // The naming of the lock could be confusing because Set must be locked by WriteLock.
        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await context.contextLock.UpgradeableLockAsync())
        {
            // if someone else did read the graph while this thread was blocekd it could be that this is already Clean
            if (State == CacheState.CacheClean && context.CurrentReaction == null)
            {
                Thread.MemoryBarrier();
                return value;
            }

            if (context.CurrentReaction != null)
            {
                context.CheckDependenciesTheSame(this);
            }

            await UpdateIfNecessary();
        }

        Thread.MemoryBarrier();
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
        var oldValue = value;

        /* Evalute the reactive function body, dynamically capturing any other reactives used */
        var prevReaction = context.CurrentReaction;
        var prevGets = context.CurrentGets;
        var prevIndex = context.CurrentGetsIndex;

        context.CurrentReaction = this;
        context.CurrentGets = Array.Empty<MemoHandlR<object>>();
        context.CurrentGetsIndex = 0;

        try
        {
            value = await new StructuredReduceJob<T>(fns, reduce, cancellationTokenSource).Run();

            // if the sources have changed, update source & observer links
            if (context.CurrentGets.Length > 0)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(context.CurrentGetsIndex);
                // update source up links
                if (Sources.Any() && context.CurrentGetsIndex > 0)
                {
                    Sources = Sources.Take(context.CurrentGetsIndex).Union(context.CurrentGets).ToArray();
                }
                else
                {
                    Sources = context.CurrentGets;
                }

                for (var i = context.CurrentGetsIndex; i < Sources.Length; i++)
                {
                    // Add ourselves to the end of the parent .observers array
                    var source = Sources[i];
                    if (!source.Observers.Any())
                    {
                        source.Observers = new[] { this };
                    }
                    else
                    {
                        source.Observers = source.Observers.Union((new[] { this })).ToArray();
                    }
                }
            }
            else if (Sources.Any() && context.CurrentGetsIndex < Sources.Length)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(context.CurrentGetsIndex);
                Sources = Sources.Take(context.CurrentGetsIndex).ToArray();
            }
        }
        finally
        {
            context.CurrentGets = prevGets;
            context.CurrentReaction = prevReaction;
            context.CurrentGetsIndex = prevIndex;
        }

        // handles diamond depenendencies if we're the parent of a diamond.
        if (Observers.Length > 0)
        {
            // We've changed value, so mark our children as dirty so they'll reevaluate
            for (int i = 0; i < Observers.Length; i++)
            {
                var observer = Observers[i];
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
            var swap = Array.FindIndex(source.Observers, (v) => v.Equals(this));
            source.Observers![swap] = source.Observers![source.Observers!.Length - 1];
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

        for (int i = 0; i < Observers.Length; i++)
        {
            await Observers[i].Stale(CacheState.CacheCheck);
        }
        return;
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }
}
