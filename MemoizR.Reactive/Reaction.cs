namespace MemoizR.Reactive;

public sealed class Reaction : SignalHandlR, IMemoizR
{
    private CacheState State { get; set; } = CacheState.CacheClean;
    private Func<Task> fn;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal Reaction(Func<Task> fn, Context context, string label = "Label") : base(context)
    {
        this.fn = fn;
        this.State = CacheState.CacheDirty;
        this.label = label;

        // The reaction must be initialized to build the Sources
        using(context.contextLock.WriterLock(-1))
        {
            Update().Wait();
        }
    }

    /** update() if dirty, or a parent turns out to be dirty. */
    internal Task UpdateIfNecessary()
    {
        if (State == CacheState.CacheClean)
        {
            return Task.CompletedTask;
        }

        // If we are potentially dirty, see if we have a parent who has actually changed value
        if (State == CacheState.CacheCheck)
        {
            foreach (var source in Sources)
            {
                (source as IMemoizR)?.UpdateIfNecessary(); // updateIfNecessary() can change state
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
            return Update();
        }

        // By now, we're clean
        State = CacheState.CacheClean;
        return Task.CompletedTask;
    }

    /** run the computation fn, updating the cached value */
    private async Task Update()
    {
        /* Evalute the reactive function body, dynamically capturing any other reactives used */
        var prevReaction = context.CurrentReaction;
        var prevGets = context.CurrentGets;
        var prevIndex = context.CurrentGetsIndex;

        context.CurrentReaction = this;
        context.CurrentGets = Array.Empty<MemoHandlR<object>>();
        context.CurrentGetsIndex = 0;

        try
        {
            await fn();

            // if the Sources have changed, update source & observer links
            if (context.CurrentGets.Length > 0)
            {
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
                        source.Observers = (new[] { this }).ToArray();
                    }
                    else
                    {
                        source.Observers = source.Observers.Union((new[] { this })).ToArray();
                    }
                }
            }
            else if (Sources.Any() && context.CurrentGetsIndex < Sources.Length)
            {
                Sources = Sources.Take(context.CurrentGetsIndex).ToArray();
            }
        }
        finally
        {
            context.CurrentGets = prevGets;
            context.CurrentReaction = prevReaction;
            context.CurrentGetsIndex = prevIndex;
        }

        // We've rerun with the latest values from all of our Sources.
        // This means that we no longer need to update until a signal changes
        State = CacheState.CacheClean;
    }

    Task IMemoizR.UpdateIfNecessary()
    {
        return UpdateIfNecessary();
    }

    internal async Task Stale(CacheState state)
    {
        using( await context.contextLock.WriterLockAsync(context.reactionIndex))
        {
            State = state;
            await UpdateIfNecessary();
        }
        return;
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }
}