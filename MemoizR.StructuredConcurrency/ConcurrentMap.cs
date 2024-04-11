namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentMap<T> : MemoHandlR<IEnumerable<T>>, IMemoizR, IStateGetR<IEnumerable<T>>
{
    private CacheState State { get; set; } = CacheState.CacheClean;
    private IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal ConcurrentMap(IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns, Context context) : base(context)
    {
        this.fns = fns;
        this.State = CacheState.CacheDirty;
    }

    public void Cancel()
    {
        cancellationTokenSource?.Cancel();
    }

    public async Task<IEnumerable<T>> Get()
    {
        if (State == CacheState.CacheClean && Context.CurrentReaction == null)
        {
            Thread.MemoryBarrier();
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
                cancellationTokenSource = Context.CancellationTokenSource;
                // if someone else did read the graph while this thread was blocekd it could be that this is already Clean
                if (State == CacheState.CacheClean && Context.CurrentReaction == null)
                {
                    Thread.MemoryBarrier();
                    return Value;
                }

                if (Context.CurrentReaction != null)
                {
                    Context.CheckDependenciesTheSame(this);
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

        Thread.MemoryBarrier();
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
        var oldValue = Value ?? [];
        
        /* Evaluate the reactive function body, dynamically capturing any other reactives used */
        var prevReaction = Context.CurrentReaction;
        var prevGets = Context.CurrentGets;
        var prevIndex = Context.CurrentGetsIndex;

        Context.CurrentReaction = this;
        Context.CurrentGets = [];
        Context.CurrentGetsIndex = 0;

        try
        {
            State = CacheState.Evaluating;
            Value = await new StructuredResultsJob<T>(fns, cancellationTokenSource!).Run();
            State = CacheState.CacheClean;
            // if the sources have changed, update source & observer links
            if (Context.CurrentGets.Length > 0)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.CurrentGetsIndex);
                // update source up links
                if (Sources.Any() && Context.CurrentGetsIndex > 0)
                {
                    Sources = [.. Sources.Take(Context.CurrentGetsIndex), .. Context.CurrentGets];
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
                        ? [new WeakReference<IMemoizR>(this)]
                        : [.. source.Observers, new WeakReference<IMemoizR>(this)];
                }
            }
            else if (Sources.Any() && Context.CurrentGetsIndex < Sources.Length)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.CurrentGetsIndex);
                Sources = [.. Sources.Take(Context.CurrentGetsIndex)];
            }
        }
        catch (TaskCanceledException)
        {
            State = CacheState.CacheCheck;
            throw;
        }
        finally
        {
            Context.CurrentGets = prevGets;
            Context.CurrentReaction = prevReaction;
            Context.CurrentGetsIndex = prevIndex;

            // handles diamond depenendencies if we're the parent of a diamond.
            if (Observers.Length > 0 && !oldValue.SequenceEqual(Value ?? []))
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

    ~ConcurrentMap()
    {
        Cancel();
    }
}
