namespace MemoizR.Reactive;

public abstract class ReactionBase : SignalHandlR, IMemoizR, IDisposable
{
    private CancellationTokenSource cts = new();
    private CancellationTokenSource sourceCts = new();
    private CacheState State { get; set; } = CacheState.CacheClean;
    private SynchronizationContext? synchronizationContext;
    private bool isPaused;

    public TimeSpan DebounceTime { private get; init; }

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

    public Task Resume()
    {
        isPaused = false;
        return UpdateIfNecessary();
    }

    public void Dispose()
    {
        Pause();
        sourceCts.Cancel();
        RemoveParentObservers(0);
    }

    protected async Task Init()
    {
        using (await Context.ContextLock.UpgradeableLockAsync())
        {
            isStartingComponent = Context.CancellationTokenSource == null;
            Context.CancellationTokenSource ??= new CancellationTokenSource();
            cancellationTokenSource = Context.CancellationTokenSource;
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
                if (isStartingComponent)
                {
                    Context.CancellationTokenSource = null;
                }
                isStartingComponent = false;
            }
        }
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
                    sourceCts = new CancellationTokenSource();
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
            if (Context.CurrentGets.Length > 0)
            {
                // remove all old Sources' .observers links to us
                RemoveParentObservers(Context.CurrentGetsIndex);
                // Update source up links.
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
                    // Add ourselves to the end of the parent .observers array.
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

    internal Task Stale(CacheState state)
    {
        // Add Scheduling
        lock (this)
        {
            State = state;
            cts?.Cancel();
            cts = new();
            Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(DebounceTime, cts.Token);

                        using (await Context.Mutex.LockAsync())
                        using (await Context.ContextLock.UpgradeableLockAsync())
                        {
                            isStartingComponent = Context.CancellationTokenSource == null;
                            Context.CancellationTokenSource ??= new CancellationTokenSource();
                            cancellationTokenSource = Context.CancellationTokenSource;
                            await UpdateIfNecessary().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                        }
                    }
                    finally
                    {
                        if (isStartingComponent)
                        {
                            Context.CancellationTokenSource = null;
                        }
                        isStartingComponent = false;
                    }
                });

            return Task.CompletedTask;
        }
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state);
    }
}
