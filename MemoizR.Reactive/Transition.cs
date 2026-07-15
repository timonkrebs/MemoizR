namespace MemoizR.Reactive;

// The ambient transition tag (ADR 0007): set on the writing flow by BeginTransition and read by
// ReactionBase.Stale. A Set's invalidation cascade runs synchronously on the writing flow, so
// the tag reaches every transitively invalidated reaction with no extra plumbing -- the same
// AsyncLocal pattern as RaceBranchFlow.
internal static class TransitionFlow
{
    internal static readonly AsyncLocal<Transition?> Current = new();

    // Detached runtime flows (debounced updates, pending-publish pumps) inherit the writing
    // flow's ExecutionContext -- and with it the tag. Their incidental Stales (commit-refused
    // renotifies, pending-signal propagation) are machinery, not user writes: left tagged, a
    // transition's own Pending signal would re-register that signal's observers on the
    // transition forever and it could never settle. Suppress() cuts the inheritance at the
    // detachment point; inside an async method the write stays local to that method's flow.
    internal static void Suppress() => Current.Value = null;
}

/// <summary>
/// One write wavefront (ADR 0007): the reactions invalidated by the Sets performed inside the
/// scope, tracked until every one of them has committed clean again. <c>using</c> the scope
/// seals the wavefront on dispose; <c>await using</c> additionally awaits <see cref="Settled"/>
/// -- the onSettled analog. Faulted effects surface structured-concurrency-style: Settled
/// faults with an <see cref="AggregateException"/> of every reaction fault in the wavefront.
/// A paused reaction keeps the transition pending until a Resume commits; a disposed reaction
/// releases it.
/// </summary>
public sealed class Transition : IDisposable, IAsyncDisposable, IStabilizationListener
{
    private readonly Lock gate = new();
    private readonly Context context;
    private readonly Transition? prior;
    // Per reached reaction, the highest invalidation generation a tagged Stale registered: a
    // commit reflects the wavefront's writes to that reaction exactly when its token is >= this
    // threshold (see IStabilizationListener).
    private readonly Dictionary<ReactionBase, int> outstanding = new();
    private readonly Dictionary<ReactionBase, Exception> faults = new();
    private readonly TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Signal<bool>? pendingSignal;
    private Task pendingPublishChain = Task.CompletedTask;
    private volatile bool isPending;
    private bool isSealed;

    internal Transition(Context context)
    {
        this.context = context;
        prior = TransitionFlow.Current.Value;
        TransitionFlow.Current.Value = this;
    }

    /// <summary>Snapshot: is any reached reaction still awaiting its clean commit?</summary>
    public bool IsPending => isPending;

    /// <summary>
    /// The reactive projection of <see cref="IsPending"/> -- an ordinary graph node, so
    /// spinners and disabled-states are just reactions on it. Published from a detached runtime
    /// flow; it converges on the snapshot but individual flips can lag it.
    /// </summary>
    public IStateGetR<bool> Pending
    {
        get
        {
            if (pendingSignal is null)
            {
                LazyInitializer.EnsureInitialized(ref pendingSignal,
                    () => new Signal<bool>(isPending, context) { Label = "Transition.Pending" });
                // Fold any state change that raced the lazy creation into the signal.
                SchedulePendingPublish();
            }
            return pendingSignal!;
        }
    }

    /// <summary>
    /// Completes when the scope has been disposed (sealing the wavefront) AND every reached
    /// reaction has committed clean; faults with an AggregateException when any reached
    /// reaction's update threw instead.
    /// </summary>
    public Task Settled => settled.Task;

    /// <summary>Seals the wavefront and restores the prior ambient transition tag.</summary>
    public void Dispose()
    {
        TransitionFlow.Current.Value = prior;
        bool completed;
        lock (gate)
        {
            isSealed = true;
            completed = outstanding.Count == 0 && !settled.Task.IsCompleted;
        }
        if (completed)
        {
            CompleteSettled();
        }
    }

    /// <summary>Seals the wavefront, then awaits <see cref="Settled"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await settled.Task.ConfigureAwait(false);
    }

    // A tagged invalidation reached `reaction` (called by ReactionBase.Stale, outside its
    // staleLock). Registration order vs. the scheduled update is free: a commit that races the
    // registration window is recovered by the LastCleanCommitToken check below, and a
    // dispose that races it by the IsDisposed check -- both re-drive the same threshold logic
    // the listener notification would have.
    internal void RegisterReached(ReactionBase reaction, int threshold)
    {
        var subscribe = false;
        lock (gate)
        {
            if (settled.Task.IsCompleted)
            {
                // A fully settled transition is immutable; a Set performed on a flow that
                // still carries the tag after settlement is not tracked.
                return;
            }

            // A newer invalidation supersedes a recorded fault: the reaction gets another
            // chance to commit inside this wavefront.
            faults.Remove(reaction);
            if (outstanding.TryGetValue(reaction, out var existing))
            {
                if (threshold > existing)
                {
                    outstanding[reaction] = threshold;
                }
            }
            else
            {
                outstanding[reaction] = threshold;
                subscribe = true;
            }
            isPending = true;
        }

        if (subscribe)
        {
            reaction.AddStabilizationListener(this);
        }
        OnStabilizedCore(reaction, reaction.stateCell.LastCleanCommitToken);
        if (reaction.IsDisposed)
        {
            OnStabilizedCore(reaction, int.MaxValue);
        }
        SchedulePendingPublish();
    }

    void IStabilizationListener.OnStabilized(SignalHandlR node, int token)
    {
        if (node is ReactionBase reaction)
        {
            OnStabilizedCore(reaction, token);
        }
    }

    private void OnStabilizedCore(ReactionBase reaction, int token)
    {
        bool remove;
        var completed = false;
        lock (gate)
        {
            remove = outstanding.TryGetValue(reaction, out var threshold) && token >= threshold;
            if (remove)
            {
                outstanding.Remove(reaction);
                isPending = outstanding.Count > 0;
                completed = isSealed && outstanding.Count == 0 && !settled.Task.IsCompleted;
            }
        }
        if (remove)
        {
            reaction.RemoveStabilizationListener(this);
            SchedulePendingPublish();
        }
        if (completed)
        {
            CompleteSettled();
        }
    }

    void IStabilizationListener.OnStabilizationFaulted(SignalHandlR node, Exception exception)
    {
        if (node is not ReactionBase reaction)
        {
            return;
        }

        bool remove;
        var completed = false;
        lock (gate)
        {
            remove = outstanding.Remove(reaction);
            if (remove)
            {
                faults[reaction] = exception;
                isPending = outstanding.Count > 0;
                completed = isSealed && outstanding.Count == 0 && !settled.Task.IsCompleted;
            }
        }
        if (remove)
        {
            reaction.RemoveStabilizationListener(this);
            SchedulePendingPublish();
        }
        if (completed)
        {
            CompleteSettled();
        }
    }

    private void CompleteSettled()
    {
        Exception[] recorded;
        lock (gate)
        {
            recorded = [.. faults.Values];
        }

        if (recorded.Length > 0)
        {
            settled.TrySetException(new AggregateException(recorded));
        }
        else
        {
            settled.TrySetResult();
        }
    }

    // Publish the current pending state into the reactive signal from a detached flow: the
    // callers run inside committing evaluations (OnStabilized) or invalidation cascades
    // (RegisterReached), where a Set would re-enter the graph. Publishes are chained so they
    // apply in schedule order, and each link reads the CURRENT snapshot at Set time -- every
    // state change schedules a link, so the last link always publishes the latest truth no
    // matter how the flips raced.
    private void SchedulePendingPublish()
    {
        if (pendingSignal is null)
        {
            return;
        }

        lock (gate)
        {
            var prev = pendingPublishChain;
            pendingPublishChain = Task.Run(async () =>
            {
                TransitionFlow.Suppress();
                await prev.ConfigureAwait(false);
                try
                {
                    await pendingSignal!.Set(isPending).ConfigureAwait(false);
                }
                catch
                {
                    // A failed publish must not break the chain; the next state change
                    // schedules another link that converges the signal.
                }
            });
        }
    }
}
