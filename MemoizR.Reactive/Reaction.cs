namespace MemoizR;

public sealed class Reaction<T> : MemoHandlR<T>, IMemoizR
{
    private CacheState State { get; set; } = CacheState.CacheClean;
    private Func<T?> fn;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal Reaction(Func<T> fn, Context context, string label = "Label", Func<T?, T?, bool>? equals = null) : base(context, equals)
    {
        this.fn = fn;
        this.State = CacheState.CacheDirty;
        this.label = label;

        // The reaction must be initialized to build the sources
        context.contextLock.EnterWriteLock();
        try
        {
            Update();
        }
        finally
        {
            context.contextLock.ExitWriteLock();
        }
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
            fn();

            // if the sources have changed, update source & observer links
            if (context.CurrentGets.Length > 0)
            {
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
                sources = sources.Take(context.CurrentGetsIndex).ToArray();
            }
        }
        finally
        {
            context.CurrentGets = prevGets;
            context.CurrentReaction = prevReaction;
            context.CurrentGetsIndex = prevIndex;
        }

        // We've rerun with the latest values from all of our sources.
        // This means that we no longer need to update until a signal changes
        State = CacheState.CacheClean;
    }

    void IMemoizR.UpdateIfNecessary()
    {
        UpdateIfNecessary();
    }

    internal void Stale(CacheState state)
    {
        if (state <= State)
        {
            UpdateIfNecessary();
            return;
        }

        State = state;
        UpdateIfNecessary();
    }

    void IMemoizR.Stale(CacheState state)
    {
        Stale(state);
    }
}