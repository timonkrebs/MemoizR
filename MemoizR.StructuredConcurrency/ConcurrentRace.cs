namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentRace<T> : SignalHandlR, IMemoizR
{
    private CacheState State { get; set; } = CacheState.CacheDirty;
    private IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns;
    private readonly CancellationTokenSource cancellationTokenSource;
    private T? value = default;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal ConcurrentRace(IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns, Context context, CancellationTokenSource cancellationTokenSource, string label = "Label") : base(context)
    {
        this.fns = fns;
        this.cancellationTokenSource = cancellationTokenSource;
        this.Label = label;
    }

    public async Task<T?> Get()
    {
        // The naming of the lock could be confusing because Set must be locked by WriteLock.
        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await Context.ContextLock.UpgradeableLockAsync())
        {

            await Update();
        }

        Thread.MemoryBarrier();
        return value;
    }

    /** run the computation fn, updating the cached value */
    private async Task Update()
    {
        var oldValue = value;

        /* Evaluate the reactive function body, dynamically capturing any other reactives used */
        var prevReaction = Context.CurrentReaction;
        var prevGets = Context.CurrentGets;
        var prevIndex = Context.CurrentGetsIndex;

        Context.CurrentReaction = this;
        Context.CurrentGets = Array.Empty<IMemoHandlR>();
        Context.CurrentGetsIndex = 0;

        try
        {
            value = await new StructuredRaceJob<T>(fns, cancellationTokenSource).Run();

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
            var swap = Array.FindIndex(source.Observers, (v) => v.Equals(this));
            source.Observers[swap] = source.Observers[source.Observers!.Length - 1];
            source.Observers = source.Observers.SkipLast(1).ToArray();
        }
    }

    Task IMemoizR.UpdateIfNecessary()
    {
        return Update();
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Task.CompletedTask;
    }
}
