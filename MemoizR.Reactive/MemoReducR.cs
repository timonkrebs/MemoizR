namespace MemoizR;

public sealed class MemoReducR<T> : MemoHandlR<T>, IMemoizR
{
    private CacheState State { get; set; } = CacheState.CacheClean;
    private Func<T?, T> fn;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal MemoReducR(Func<T?, T> fn, Context context, string label = "Label", Func<T?, T?, bool>? equals = null) : base(context, equals)
    {
        this.fn = fn;
        this.State = CacheState.CacheDirty;
        this.label = label;
    }

    public T? Get()
    {
        if (State == CacheState.CacheClean && context.CurrentReaction == null)
        {
            return value;
        }

        // only one thread should evaluate the graph at a time. <otherwise the context could get messed up
        context.contextLock.EnterWriteLock();
        try
        {
            // if someone else did read the graph while this thread was blocekd it could be that this is already Clean
            if (State == CacheState.CacheClean && context.CurrentReaction == null)
            {
                return value;
            }

            if (context.CurrentReaction != null)
            {
                if ((context.CurrentGets == null || !(context.CurrentGets.Length > 0)) &&
                  (context.CurrentReaction.sources != null && context.CurrentReaction.sources.Length > 0) &&
                  context.CurrentReaction.sources[context.CurrentGetsIndex].Equals(this)
                )
                {
                    context.CurrentGetsIndex++;
                }
                else
                {
                    if (!context.CurrentGets!.Any()) context.CurrentGets = new[] { this };
                    else context.CurrentGets = context.CurrentGets!.Union(new[] { this }).ToArray();
                }
            }

            UpdateIfNecessary();
        }
        finally
        {
            context.contextLock.ExitWriteLock();
        }

        return value;
    }

    /** update() if dirty, or a parent turns out to be dirty. */
    internal void UpdateIfNecessary()
    {
        if (State == CacheState.CacheClean)
        {
            return;
        }

        // If we are potentially dirty, see if we have a parent who has actually changed value
        if (State == CacheState.CacheCheck)
        {
            foreach (var source in sources)
            {
                (source as IMemoizR)?.UpdateIfNecessary(); // updateIfNecessary() can change state
                if (State == CacheState.CacheDirty)
                {
                    // Stop the loop here so we won't trigger updates on other parents unnecessarily
                    // If our computation changes to no longer use some sources, we don't
                    // want to update() a source we used last time, but now don't use.
                    break;
                }
            }
        }

        // If we were already dirty or marked dirty by the step above, update.
        if (State == CacheState.CacheDirty)
        {
            Update();
        }

        // By now, we're clean
        State = CacheState.CacheClean;
    }

    /** run the computation fn, updating the cached value */
    private void Update()
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
            value = fn(value);

            // if the sources have changed, update source & observer links
            if (context.CurrentGets.Length > 0)
            {
                // remove all old sources' .observers links to us
                RemoveParentObservers(context.CurrentGetsIndex);
                // update source up links
                if (sources.Any() && context.CurrentGetsIndex > 0)
                {
                    sources = sources.Take(context.CurrentGetsIndex).Union(context.CurrentGets).ToArray();
                }
                else
                {
                    sources = context.CurrentGets;
                }

                for (var i = context.CurrentGetsIndex; i < sources.Length; i++)
                {
                    // Add ourselves to the end of the parent .observers array
                    var source = sources[i];
                    if (!source.Observers.Any())
                    {
                        source.Observers = (new[] { this }).ToArray();
                    }
                    else
                    {
                        source.Observers = source.Observers.Union((new[] { this })).ToArray();
                    }
                }
            }
            else if (sources.Any() && context.CurrentGetsIndex < sources.Length)
            {
                // remove all old sources' .observers links to us
                RemoveParentObservers(context.CurrentGetsIndex);
                sources = sources.Take(context.CurrentGetsIndex).ToArray();
            }
        }
        finally
        {
            context.CurrentGets = prevGets;
            context.CurrentReaction = prevReaction;
            context.CurrentGetsIndex = prevIndex;
        }

        // handles diamond depenendencies if we're the parent of a diamond.
        if (!equals(oldValue, value) && Observers.Length > 0)
        {
            // We've changed value, so mark our children as dirty so they'll reevaluate
            for (int i = 0; i < Observers.Length; i++)
            {
                var observer = Observers[i];
                observer.State = CacheState.CacheDirty;
            }
        }

        // We've rerun with the latest values from all of our sources.
        // This means that we no longer need to update until a signal changes
        State = CacheState.CacheClean;
    }

    private void RemoveParentObservers(int index)
    {
        if (!sources.Any()) return;
        for (var i = index; i < sources.Length; i++)
        {
            var source = sources[i]; // We don't actually delete sources here because we're replacing the entire array soon
            var swap = Array.FindIndex(source.Observers, (v) => v.Equals(this));
            source.Observers![swap] = source.Observers![source.Observers!.Length - 1];
            source.Observers = source.Observers.SkipLast(1).ToArray();
        }
    }

    void IMemoizR.UpdateIfNecessary()
    {
        UpdateIfNecessary();
    }

    internal void Stale(CacheState state)
    {
        if (state <= State)
        {
            Update();
            return;
        }

        Update();

        for (int i = 0; i < Observers.Length; i++)
        {
            Observers[i].Stale(CacheState.CacheCheck);
        }
    }

    void IMemoizR.Stale(CacheState state)
    {
        Stale(state);
    }
}