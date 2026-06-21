namespace MemoizR.Reactive;

public abstract class ReactionBase : SignalHandlR, IMemoizR, IDisposable
{
    // Guards the debounce scheduling state (cts swap + invalidate + spawn). A dedicated lock
    // object, NOT lock(this): the reaction instance is public, so user code locking it could
    // deadlock the whole invalidation cascade.
    private readonly Lock staleLock = new();
    private CancellationTokenSource cts = new();
    private volatile bool disposed;
    // State is invalidated by Stale (under lock(this), driven by a Set on another flow) and
    // committed by the recompute under the node mutex + ContextLock. The inherited cell's
    // generation guard stops the recompute from committing Clean over a Stale that arrived
    // mid-evaluation -- the cross-flow lost-update race, which for a reaction means a missed
    // trigger (see CacheStateCell). isPaused is written by Pause/Resume from arbitrary threads
    // and read in Update, so it stays volatile.
    private CacheState State => stateCell.State;
    private SynchronizationContext? synchronizationContext;
    // The clock the debounce delay runs on (issue #38). Threaded through the constructor like
    // synchronizationContext so tests can drive the debounce window with a fake provider instead
    // of racing wall-clock time. Null defaults to TimeProvider.System.
    private readonly TimeProvider timeProvider;
    private volatile bool isPaused;

    public TimeSpan DebounceTime { protected get; init; }

    // Written by a parent's diamond down-link; absorbed (not generation-bumped) during our own eval.
    CacheState IMemoizR.State { get => stateCell.State; set => stateCell.InvalidateFromParent(value); }

    internal ReactionBase(Context context, SynchronizationContext? synchronizationContext = null, TimeProvider? timeProvider = null)
    : base(context)
    {
        this.synchronizationContext = synchronizationContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        stateCell.Force(CacheState.CacheDirty);
    }

    public void Pause()
    {
        isPaused = true;
    }

    public async Task Resume()
    {
        isPaused = false;
        if (ResumeOnDetachedScope)
        {
            await Task.Run(RunResumeUpdateOnDetachedScope);
            return;
        }

        await RunResumeUpdateOnCurrentScope();
    }

    protected virtual bool ResumeOnDetachedScope => false;

    private async Task RunResumeUpdateOnCurrentScope()
    {
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
        // Strong root for the weakly-registered scope: keeps the held lock's identity stable for
        // the whole update (a collected scope would resurrect with a fresh, free ContextLock).
        var scope = Context.ReactionScope;
        try
        {
            using (await mutex.LockAsync())
            using (await scope.ContextLock.UpgradeableLockAsync())
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
            GC.KeepAlive(scope);
        }
    }

    private async Task RunResumeUpdateOnDetachedScope()
    {
        // Plain Reaction keeps dependency evaluation off the caller's thread even for Resume().
        // This mirrors the debounced update path: the action may marshal through the builder's
        // SynchronizationContext, but the graph is evaluated on a fresh worker-owned scope.
        var scope = Context.ForceNewScope();
        try
        {
            using (await mutex.LockAsync())
            using (await scope.ContextLock.UpgradeableLockAsync())
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
            Context.CleanScope();
            GC.KeepAlive(scope);
        }
    }

    public void Dispose()
    {
        disposed = true;
        Pause();
        CancellationTokenSource pending;
        lock (staleLock)
        {
            pending = cts;
        }
        // Cancel OUTSIDE the monitor: canceling runs the pending debounced update's delay
        // continuation inline on this stack. Dropping it means the dead reaction's update never
        // re-acquires locks or re-runs the parent scan.
        pending.Cancel();
        RemoveParentObservers();
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

        // If we are potentially dirty, check if a parent has actually changed value.
        var parentFaulted = State == CacheState.CacheCheck && await ScanParentsForDirty();

        // If we were already dirty or marked dirty by the step above, update.
        if (State == CacheState.CacheDirty)
        {
            await Update();
        }

        // A parent that faulted never resolved this node's CacheCheck: committing Clean over it
        // would stop all future re-checks (nothing re-dirties us). Stay CacheCheck so the next
        // trigger re-attempts the parent.
        if (parentFaulted)
        {
            return;
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
                // A reaction has no pull path: if a FIRST run throws with no links wired, no
                // future Set could ever re-trigger it -- orphaned forever -- so wire whatever it
                // captured. A RE-run that throws keeps its previous links untouched instead:
                // rewiring from the partial capture would strip the not-yet-read tail (or
                // everything, when the body threw before its first read), and a Set on those
                // sources would never revive the reaction. Stale-but-kept links only cost a
                // spurious re-run, which the next successful pass prunes. Memos deliberately do
                // neither: they keep their links and retry via the pull path on the next Get.
                if (Sources.Length == 0)
                {
                    UpdateSourceAndObserverLinks();
                }
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

    // Run Execute on the captured SynchronizationContext if one was supplied (marshalling the
    // result/exception back through a TaskCompletionSource), otherwise run it inline. Only
    // AdvancedReaction supplies a context here -- its opaque body cannot be split, so it runs on
    // the context as a whole. Reaction marshals at action granularity inside its composed body
    // instead (ReactionBuilder.InvokeActionAsync), keeping dependency evaluation off the context.
    private async Task InvokeExecute()
    {
        if (synchronizationContext != null)
        {
            // RunContinuationsAsynchronously: completing the TCS must not run the rest of the
            // update pipeline (link rewiring, state commit, lock releases) inline inside the
            // SynchronizationContext's posted callback -- that work belongs to the update's own
            // flow, and running it on e.g. a UI thread inside the post both blocks that thread
            // and exposes the pipeline to whatever exception handling wraps the context's
            // callbacks.
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async void SendOrPostCallback(object? _)
            {
                // Complete the TCS exactly once: SetResult after SetException would throw
                // InvalidOperationException out of this async void and crash the process via the
                // SynchronizationContext instead of faulting the awaited task.
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

            synchronizationContext.Post(SendOrPostCallback, null);
            await tcs.Task;
        }
        else
        {
            await Execute();
        }
    }

    // The IMemoizR fan-in (a parent scanning this reaction) reaches the locked update through the
    // shared scaffold. The reaction's OWN debounced/Resume paths do not: they own or conditionally
    // tear down a scope of their own, which this GetOrCreateScope-based helper does not model.
    Task IMemoizR.UpdateIfNecessary()
    {
        return Context.UpdateUnderLockAsync(mutex, UpdateIfNecessary);
    }

    internal Task Stale(CacheState state, TimeSpan debounceTime)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        CancellationTokenSource superseded;
        lock (staleLock)
        {
            // Escalate and bump the generation so an in-flight recompute can't commit Clean over
            // this invalidation; the debounce below still (re)schedules the update regardless.
            stateCell.Invalidate(state);
            superseded = cts;
            cts = new();

            // Open the cancellable window NOW, not when the debounced update starts running:
            // Cancel() must be able to abort an update that is scheduled but still waiting out
            // its debounce. Every Stale spawns exactly one RunDebouncedUpdateAsync, whose
            // outermost finally exits the scope (including the superseded-early-return path),
            // so the refcount stays balanced under coalescing. Entering BEFORE cancelling the
            // superseded task below also keeps the refcount from dipping to zero mid-supersession
            // (a dip tears down the shared CancellationTokenSource under live evaluations).
            Context.EnterEvaluationScope();

            // Fire-and-forget the debounced update. A newer Stale cancels this token, so a
            // superseded update is skipped entirely instead of running anyway (the previous
            // ContinueWith ran even on cancellation, flooding the thread pool with redundant
            // updates and starving it).
            _ = RunDebouncedUpdateAsync(debounceTime, cts.Token);
        }

        // Cancel OUTSIDE the monitor: cancellation runs the superseded task's delay continuation
        // inline on this stack, and that continuation takes other locks (ExitEvaluationScope).
        superseded.Cancel();

        return Task.CompletedTask;
    }

    private async Task RunDebouncedUpdateAsync(TimeSpan debounceTime, CancellationToken token)
    {
        // Balances the EnterEvaluationScope performed by the Stale that spawned this task --
        // on the full-run path AND the superseded-early-return path.
        try
        {
            // Hop off the caller's stack first: with a zero debounce, Task.Delay completes
            // synchronously (plain ConfigureAwait(false) does not yield on a completed task)
            // and this "fire-and-forget" would otherwise run the WHOLE update -- including the
            // user's Execute body -- inline inside Stale's monitor (and, for the initial run,
            // inside the builder's factory lock). ForceYielding instead of an extra Task.Yield:
            // it always yields AND never captures the scheduling thread's SynchronizationContext
            // (Task.Yield does), so a Stale raised on a UI thread -- Set callers and reaction
            // builders under MemoizR.Wpf -- continues on the thread pool instead of queueing the
            // whole dependency-graph evaluation back onto the UI thread (#13).
            try
            {
                await Task.Delay(debounceTime, timeProvider, token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer Stale before the debounce elapsed; nothing to do.
                return;
            }

            // The node mutex serializes this update against a concurrent Resume() or another
            // debounced update of the same reaction (each may run on a different flow, so the
            // ContextLock alone does not order them): without it, a stale in-flight Execute
            // could apply its side effects after a newer update finished and committed Clean.
            //
            // The update runs on a scope it OWNS (ForceNewScope), never the inherited one: this
            // detached task inherits the triggering Set's AsyncLocal flow, and two debounced
            // updates sharing that flow would each be granted the flow's ContextLock as a
            // "recursive" same-scope acquisition -- running concurrently on one ReactionScope and
            // corrupting each other's dependency capture. The local is also the scope's only
            // strong root (the registry holds it weakly).
            var scope = Context.ForceNewScope();
            try
            {
                using (await mutex.LockAsync())
                using (await scope.ContextLock.UpgradeableLockAsync())
                {
                    await UpdateIfNecessary().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            finally
            {
                Context.CleanScope();
                GC.KeepAlive(scope);
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

    // Eager-run contract: a reaction executes once on creation (SolidJS-style effect semantics),
    // scheduled through the same invalidation/debounce machinery as every other trigger.
    // TimeSpan.Zero deliberately bypasses the configured DebounceTime -- the initial run should
    // not wait out a debounce window meant for write coalescing. Called by the BUILDER after the
    // object initializer completes, never from a constructor: a Set racing the construction
    // reaches IMemoizR.Stale, which reads DebounceTime, and from inside the constructor it would
    // observe the unassigned default.
    internal void ScheduleInitialRun()
    {
        Stale(CacheState.CacheDirty, TimeSpan.Zero);
    }
}
