namespace MemoizR.Reactive;

public sealed class Reaction : SignalHandlR, IMemoizR
{
    private CacheState State { get; set; } = CacheState.CacheClean;
    private Func<Task> fn;
    private SynchronizationContext? sheduler;
    private bool isPaused;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal Reaction(Func<Task> fn, Context context, SynchronizationContext? sheduler = null, string label = "Label") : base(context)
    {
        this.fn = fn;
        this.sheduler = sheduler;
        this.State = CacheState.CacheDirty;
        this.label = label;

        _ = Init();
    }

    public void Pause()
    {
        this.isPaused = true;
    }

    public Task Resume()
    {
        this.isPaused = false;
        return UpdateIfNecessary();
    }

    private async Task Init()
    {
        using (await context.contextLock.UpgradeableLockAsync())
        {
            var s = sheduler;
            try
            {
                sheduler = null;
                // The reaction must be initialized to build the Sources.
                await Update();
            }
            finally
            {
                sheduler = s;
            }
        }
    }

    // Update the reaction if dirty, or a parent turns out to be dirty.
    internal async Task UpdateIfNecessary()
    {
        if (State == CacheState.CacheClean)
        {
            return;
        }

        // If we are potentially dirty, check if we have a parent who has actually changed value.
        if (State == CacheState.CacheCheck)
        {
            foreach (var source in Sources)
            {
                if (source is IMemoizR memoizR)
                {
                    await memoizR.UpdateIfNecessary(); // updateIfNecessary() can change state.
                }

                if (State == CacheState.CacheDirty)
                {
                    // Stop the loop here so we won't trigger updates on other parents unnecessarily.
                    // If our computation changes to no longer use some Sources, we don't
                    // want to update() a source we used last time but now don't use.
                    break;
                }
            }
        }

        // If we were already dirty or marked dirty by the step above, update.
        if (State == CacheState.CacheDirty)
        {
            await Update();
        }

        // By now, we're clean.
        State = CacheState.CacheClean;
    }

    // Update the cached value by running the computation.
    private async Task Update()
    {
        if (isPaused)
        {
            State = CacheState.CacheDirty;
            return;
        }

        // Evaluate the reactive function body, dynamically capturing any other reactives used.
        var prevReaction = context.CurrentReaction;
        var prevGets = context.CurrentGets;
        var prevIndex = context.CurrentGetsIndex;

        context.CurrentReaction = this;
        context.CurrentGets = Array.Empty<MemoHandlR<object>>();
        context.CurrentGetsIndex = 0;

        try
        {
            if (!isPaused)
            {
                if (sheduler != null)
                {
                    var tcs = new TaskCompletionSource();
                    sheduler.Post(async _ =>
                    {
                        await fn();
                        tcs.SetResult();
                    }, null);
                    await tcs.Task;
                }
                else
                {
                    await fn();
                }
            }
            else
            {
                State = CacheState.CacheDirty;
                return;
            }

            // If the Sources have changed, update source & observer links.
            if (context.CurrentGets.Length > 0)
            {
                // Update source up links.
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
                    // Add ourselves to the end of the parent .observers array.
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
        // This means that we no longer need to update until a signal changes.
        State = CacheState.CacheClean;
    }

    async Task IMemoizR.UpdateIfNecessary()
    {
        using (await context.contextLock.UpgradeableLockAsync())
        {
            await UpdateIfNecessary();
        }
    }

    internal Task Stale(CacheState state)
    {
        State = state;
        _ = UpdateIfNecessary();

        return Task.CompletedTask;
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }
}
