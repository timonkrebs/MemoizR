namespace MemoizR.Reactive;

public abstract class ReactionBase : SignalHandlR, IMemoizR, IDisposable
{
    private CancellationTokenSource cts = new();
    // State is invalidated by Stale (under lock(this), driven by a Set on another flow) and
    // committed by the recompute under the node mutex + ContextLock. The inherited cell's
    // generation guard stops the recompute from committing Clean over a Stale that arrived
    // mid-evaluation -- the cross-flow lost-update race, which for a reaction means a missed
    // trigger (see CacheStateCell). isPaused is written by Pause/Resume from arbitrary threads
    // and read in Update, so it stays volatile.
    private CacheState State => stateCell.State;
    private readonly IExecutor? executor;
    private volatile bool isPaused;

    public TimeSpan DebounceTime { protected get; init; }

    // Written by a parent's diamond down-link; absorbed (not generation-bumped) during our own eval.
    CacheState IMemoizR.State { get => stateCell.State; set => stateCell.InvalidateFromParent(value); }

    internal ReactionBase(Context context, IExecutor? executor = null)
    : base(context)
    {
        this.executor = executor;
        stateCell.Force(CacheState.CacheDirty);
    }

    public void Pause()
    {
        isPaused = true;
    }

    public async Task Resume()
    {
        isPaused = false;
        // Serialize like the debounced update path: the node mutex ensures only one update of
        // this reaction runs at a time (Resume vs concurrent debounced updates -- without it, a
        // stale in-flight Execute could apply its side effects after a newer one finished), and
        // the ContextLock serializes graph evaluation within this flow. The ContextLock is
        // per-flow, so it does NOT order this update against Sets on other flows; cross-flow
        // correctness comes from the CacheStateCell generation guard plus the whole-array swaps
        // behind the IMemoHandlR setters (see ADR 0001/0002). Like the debounced path, the
        // reaction body must not call Signal.Set on its own flow (exclusive-inside-upgradeable
        // is rejected by the lock). Only clean up the flow scope if we created it -- Resume can
        // be called from inside an active evaluation whose scope must survive this call.
        var createdScope = Context.CreateNewScopeIfNeeded();
        try
        {
            using (await mutex.LockAsync())
            using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
            {
                Context.EnterEvaluationScope();
                try
                {
                    await UpdateIfNecessary();
                }
                finally
                {
                    Context.ExitEvaluationScope();
                }
            }
        }
        finally
        {
            if (createdScope)
            {
                Context.CleanScope();
            }
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

        // Snapshot the generation up front: if a Stale invalidates us while we check parents or
        // recompute, the final commit below must not mark us Clean over it.
        var token = stateCell.Generation;

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

        // By now, we're clean -- unless a Stale invalidated us along the way, in which case stay
        // dirty so the debounced update scheduled by that Stale re-runs us.
        stateCell.TryCommitClean(token);
    }

    // Update the cached value by running the computation.
    private async Task Update()
    {
        if (isPaused)
        {
            stateCell.Force(CacheState.CacheDirty);
            return;
        }

        /* Evaluate the reactive function body, dynamically capturing any other reactives used.
           Resolve the scope once -- it is stable for the whole evaluation (same flow), and the
           local additionally keeps the weakly-held scope strongly referenced until restored. */
        var scope = Context.ReactionScope;
        var prevReaction = scope.CurrentReaction;
        var prevGets = scope.CurrentGets;
        var prevIndex = scope.CurrentGetsIndex;

        scope.CurrentReaction = this;
        scope.CurrentGets = [];
        scope.CurrentGetsIndex = 0;

        // Mark Evaluating and snapshot the generation so a Stale during Execute escalates past
        // Evaluating (bumping the generation) and blocks the commit below.
        var token = stateCell.BeginEvaluation();
        try
        {
            // isPaused may have flipped between the top-of-method guard and here.
            if (isPaused)
            {
                stateCell.Force(CacheState.CacheDirty);
                return;
            }

            try
            {
                await InvokeExecute();
            }
            catch
            {
                stateCell.Force(CacheState.CacheDirty);
                // A reaction has no pull path: if the dependencies captured before the exception
                // were dropped, no future Set could ever re-trigger it -- a reaction whose FIRST
                // run throws would be orphaned forever. Wire what was captured (for a later run
                // this keeps the prefix read before the failure and prunes the rest; the next
                // successful run rewires the full set). Memos deliberately do NOT do this: they
                // keep their previous links and retry via the pull path on the next Get.
                UpdateSourceAndObserverLinks();
                throw;
            }

            UpdateSourceAndObserverLinks();
        }
        finally
        {
            scope.CurrentGets = prevGets;
            scope.CurrentReaction = prevReaction;
            scope.CurrentGetsIndex = prevIndex;
        }

        // We've rerun with the latest values from all of our Sources, so we no longer need to
        // update until a signal changes -- unless a Stale invalidated us mid-evaluation, in which
        // case stay dirty so the debounced update scheduled by that Stale re-runs us.
        stateCell.TryCommitClean(token);
    }

    // Run Execute on the configured executor if one was supplied (marshalling the
    // result/exception back through a TaskCompletionSource), otherwise run it inline. The
    // executor only decides WHERE Execute runs (SE-0392 analog); completion and exception
    // semantics live here, once, so an IExecutor implementation cannot get them wrong.
    private async Task InvokeExecute()
    {
        if (executor != null)
        {
            // RunContinuationsAsynchronously: completing the TCS must not run the rest of the
            // update pipeline (link rewiring, state commit, lock releases) inline inside the
            // executor's slot -- that work belongs to the update's own flow, and running it on
            // e.g. a UI thread inside the enqueued callback both blocks that thread and exposes
            // the pipeline to whatever exception handling wraps the executor's callbacks.
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async void ExecuteOnExecutor()
            {
                // Complete the TCS exactly once: SetResult after SetException would throw
                // InvalidOperationException out of this async void and crash the process via the
                // executor instead of faulting the awaited task. This wrapper is also what makes
                // the IExecutor contract hold ("the enqueued delegate never throws").
                try
                {
                    await Execute();
                    tcs.SetResult();
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                }
            }

            executor.Enqueue(ExecuteOnExecutor);
            await tcs.Task;
        }
        else
        {
            await Execute();
        }
    }

    async Task IMemoizR.UpdateIfNecessary()
    {
        using (await mutex.LockAsync())
        using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
        {
            Context.EnterEvaluationScope();
            try
            {
                await UpdateIfNecessary();
            }
            finally
            {
                Context.ExitEvaluationScope();
            }
        }
    }

    internal Task Stale(CacheState state, TimeSpan debounceTime)
    {
        // Add Scheduling
        lock (this)
        {
            // Escalate and bump the generation so an in-flight recompute can't commit Clean over
            // this invalidation; the debounce below still (re)schedules the update regardless.
            stateCell.Invalidate(state);
            cts?.Cancel();
            cts = new();

            // Open the cancellable window NOW, not when the debounced update starts running:
            // Cancel() must be able to abort an update that is scheduled but still waiting out
            // its debounce. Every Stale spawns exactly one RunDebouncedUpdateAsync, whose
            // outermost finally exits the scope (including the superseded-early-return path),
            // so the refcount stays balanced under coalescing.
            Context.EnterEvaluationScope();

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
        // Balances the EnterEvaluationScope performed by the Stale that spawned this task --
        // on the full-run path AND the superseded-early-return path.
        try
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

            // The node mutex serializes this update against a concurrent Resume() or another
            // debounced update of the same reaction (each inherits a different flow's ContextLock,
            // so the ContextLock alone does not order them): without it, a stale in-flight Execute
            // could apply its side effects after a newer update finished and committed Clean. Only
            // clean up the flow scope if we created it -- this task inherits the triggering Set's
            // flow, and unconditionally removing that scope would tear it down under other
            // debounced updates (or the Set caller) still using it.
            var createdScope = Context.CreateNewScopeIfNeeded();
            try
            {
                using (await mutex.LockAsync())
                using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
                {
                    await UpdateIfNecessary().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            finally
            {
                if (createdScope)
                {
                    Context.CleanScope();
                }
            }
        }
        finally
        {
            Context.ExitEvaluationScope();
        }
    }

    Task IMemoizR.Stale(CacheState state)
    {
        return Stale(state, DebounceTime);
    }
}
