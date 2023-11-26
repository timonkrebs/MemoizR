namespace MemoizR.Reactive;

public sealed class Reaction : SignalHandlR, IMemoizR, IDisposable
{
    private CancellationTokenSource? cts;
    private CacheState State { get; set; } = CacheState.CacheClean;
    private Func<Task> fn;
    private SynchronizationContext? synchronizationContext;
    private bool isPaused;

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal Reaction(Func<Task> fn, Context context, SynchronizationContext? synchronizationContext = null, string label = "Label") : base(context)
    {
        this.fn = fn;
        this.synchronizationContext = synchronizationContext;
        this.State = CacheState.CacheDirty;
        this.Label = label;

        Task.Run(async() => await Init()).GetAwaiter().GetResult();
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

    private void Dispose()
    {
        RemoveParentObservers(0);
    }

    private async Task Init()
    {
        using (await Context.ContextLock.UpgradeableLockAsync())
        {
            var temp = synchronizationContext;
            try
            {
                synchronizationContext = null;
                // The reaction must be initialized to build the Sources.
                await Update();
            }
            finally
            {
                synchronizationContext = temp;
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
                    await memoizR.UpdateIfNecessary().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // updateIfNecessary() can change state.
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
        var prevReaction = Context.CurrentReaction;
        var prevGets = Context.CurrentGets;
        var prevIndex = Context.CurrentGetsIndex;

        Context.CurrentReaction = this;
        Context.CurrentGets = [];
        Context.CurrentGetsIndex = 0;

        try
        {
            if (!isPaused)
            {
                try
                {
                    if (synchronizationContext != null)
                    {
                        var tcs = new TaskCompletionSource();

                        async void SendOrPostCallback(object? _)
                        {
                            try
                            {
                                await fn();
                            }
                            finally
                            {
                                tcs.SetResult();
                            }
                        }

                        synchronizationContext.Post(SendOrPostCallback, null);
                        await tcs.Task;
                    }
                    else
                    {
                        await fn();
                    }
                }
                catch
                {
                    State = CacheState.CacheDirty;
                    return;
                }
            }
            else
            {
                State = CacheState.CacheDirty;
                return;
            }

            // If the Sources have changed, update source & observer links.
            if (Context.CurrentGets.Length > 0)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.CurrentGetsIndex);
                // Update source up links.
                if (Sources.Any() && Context.CurrentGetsIndex > 0)
                {
                    Sources = [..Sources.Take(Context.CurrentGetsIndex), ..Context.CurrentGets];
                }
                else
                {
                    Sources = Context.CurrentGets;
                }

                for (var i = Context.CurrentGetsIndex; i < Sources.Length; i++)
                {
                    // Add ourselves to the end of the parent .observers array.
                    var source = Sources[i];
                    source.Observers = !source.Observers.Any() 
                        ? [this] 
                        : [..source.Observers, this];
                }
            }
            else if (Sources.Any() && Context.CurrentGetsIndex < Sources.Length)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.CurrentGetsIndex);
                Sources = [..Sources.Take(Context.CurrentGetsIndex)];
            }
        }
        finally
        {
            Context.CurrentGets = prevGets;
            Context.CurrentReaction = prevReaction;
            Context.CurrentGetsIndex = prevIndex;
        }

        // We've rerun with the latest values from all of our Sources.
        // This means that we no longer need to update until a signal changes.
        State = CacheState.CacheClean;
    }

    private void RemoveParentObservers(int index)
    {
        if (!Sources.Any()) return;
        foreach (var source in Sources.Skip(index))
        {
            source.Observers = [..source.Observers.Where(x => x != this)];
        }
    }

    async Task IMemoizR.UpdateIfNecessary()
    {
        using (await Context.ContextLock.UpgradeableLockAsync())
        {
            await UpdateIfNecessary();
        }
    }

    internal Task Stale(CacheState state)
    {
        State = state;
        cts?.Cancel();
        cts = new ();
        Task.Run(async() =>
        {
            await Task.Delay(10, cts.Token);
            await UpdateIfNecessary().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }, cts.Token);

        return Task.CompletedTask;
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }

    void IDisposable.Dispose()
    {
        Dispose();
    }
}
