namespace MemoizR.Reactive;

public abstract class ReactionBase : SignalHandlR, IMemoizR, IDisposable
{
    private CancellationTokenSource cts = new();
    // State is touched under two different locks (lock(this) in Stale, the ContextLock in
    // UpdateIfNecessary), and isPaused is written by Pause/Resume from arbitrary threads and
    // read in Update. Back both with volatile so the cross-lock/cross-thread reads see writes.
    private volatile CacheState state = CacheState.CacheClean;
    private CacheState State { get => state; set => state = value; }
    private SynchronizationContext? synchronizationContext;
    private volatile bool isPaused;

    public TimeSpan DebounceTime { protected get; init; }

    CacheState IMemoizR.State { get => State; set => State = value; }

    internal ReactionBase(Context context, SynchronizationContext? synchronizationContext = null)
    : base(context)
    {
        this.synchronizationContext = synchronizationContext;
        this.State = CacheState.CacheDirty;
    }

    public void Pause()
    {
        isPaused = true;
    }

    public async Task Resume()
    {
        isPaused = false;
        // Go through the ContextLock like every other update entry point (Get,
        // IMemoizR.UpdateIfNecessary, RunDebouncedUpdateAsync). Calling the internal
        // UpdateIfNecessary directly recomputes the node and rewires Sources/Observers and the
        // shared ReactionScope with no lock held, racing a concurrent Signal.Set on the same
        // context. volatile only restores visibility, not mutual exclusion, so serialize here.
        Context.CreateNewScopeIfNeeded();
        try
        {
            using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
            {
                await UpdateIfNecessary();
            }
        }
        finally
        {
            Context.CleanScope();
        }
    }

    public void Dispose()
    {
        Pause();
        RemoveParentObservers(0);
    }

    protected abstract Task Execute();

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
        var prevReaction = Context.ReactionScope.CurrentReaction;
        var prevGets = Context.ReactionScope.CurrentGets;
        var prevIndex = Context.ReactionScope.CurrentGetsIndex;

        Context.ReactionScope.CurrentReaction = this;
        Context.ReactionScope.CurrentGets = [];
        Context.ReactionScope.CurrentGetsIndex = 0;

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
                                await Execute();
                            }
                            catch (Exception e)
                            {
                                tcs.SetException(e);
                            }

                            tcs.SetResult();
                        }

                        synchronizationContext.Post(SendOrPostCallback, null);
                        await tcs.Task;
                    }
                    else
                    {
                        await Execute();
                    }
                }
                catch
                {
                    State = CacheState.CacheDirty;
                    throw;
                }
            }
            else
            {
                State = CacheState.CacheDirty;
                return;
            }

            // If the Sources have changed, update source & observer links.
            if (Context.ReactionScope.CurrentGets.Length > 0)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.ReactionScope.CurrentGetsIndex);
                // Update source up links.
                if (Sources.Any() && Context.ReactionScope.CurrentGetsIndex > 0)
                {
                    Sources = [.. Sources.Take(Context.ReactionScope.CurrentGetsIndex), .. Context.ReactionScope.CurrentGets];
                }
                else
                {
                    Sources = Context.ReactionScope.CurrentGets;
                }

                for (var i = Context.ReactionScope.CurrentGetsIndex; i < Sources.Length; i++)
                {
                    // Add ourselves to the end of the parent .observers array.
                    var source = Sources[i];
                    source.Observers = !source.Observers.Any()
                        ? [new(this)]
                        : [.. source.Observers, new(this)];
                }
            }
            else if (Sources.Any() && Context.ReactionScope.CurrentGetsIndex < Sources.Length)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.ReactionScope.CurrentGetsIndex);
                Sources = [.. Sources.Take(Context.ReactionScope.CurrentGetsIndex)];
            }
        }
        finally
        {
            Context.ReactionScope.CurrentGets = prevGets;
            Context.ReactionScope.CurrentReaction = prevReaction;
            Context.ReactionScope.CurrentGetsIndex = prevIndex;
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
            source.Observers = [.. source.Observers.Where(x => x.TryGetTarget(out var o) ? o != this : false)];
        }
    }

    async Task IMemoizR.UpdateIfNecessary()
    {
        using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
        {
            await UpdateIfNecessary();
        }
    }

    internal Task Stale(CacheState state, TimeSpan debounceTime)
    {
        // Add Scheduling
        lock (this)
        {
            State = state;
            cts?.Cancel();
            cts = new();

            Context.CancellationTokenSource ??= new();

            // Fire-and-forget the debounced update. A newer Stale cancels this token, so a
            // superseded update is skipped entirely instead of running anyway (the previous
            // ContinueWith ran even on cancellation, flooding the thread pool with redundant
            // updates and starving it).
            _ = RunDebouncedUpdateAsync(debounceTime, cts.Token);

            return Task.CompletedTask;
        }
    }

    private async Task RunDebouncedUpdateAsync(TimeSpan debounceTime, CancellationToken token)
    {
        try
        {
            await Task.Delay(debounceTime, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer Stale before the debounce elapsed; nothing to do.
            return;
        }

        Context.CreateNewScopeIfNeeded();
        try
        {
            using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
            {
                await UpdateIfNecessary().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            Context.CancellationTokenSource = null;
            Context.CleanScope();
        }
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state, DebounceTime);
    }
}
