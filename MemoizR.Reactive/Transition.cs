namespace MemoizR.Reactive;

// The ambient transition tag (ADR 0007): set on the writing flow by BeginTransition and read by
// ReactionBase.Stale. A Set's invalidation cascade runs synchronously on the writing flow, so
// the tag reaches every transitively invalidated reaction with no extra plumbing -- the same
// AsyncLocal pattern as RaceBranchFlow. Backed by the core's WavefrontFlow holder, whose
// presence also tells the core's cascade not to prune at already-dirty nodes (a pruned cascade
// would hide the tagged write from downstream registrations).
internal static class TransitionFlow
{
    internal static Transition? Current
    {
        get => WavefrontFlow.Current.Value as Transition;
        set => WavefrontFlow.Current.Value = value;
    }

    // Detached runtime flows (debounced updates, pending-publish pumps) inherit the writing
    // flow's ExecutionContext -- and with it the tag. Their incidental Stales (commit-refused
    // renotifies, pending-signal propagation) are machinery, not user writes: left tagged, a
    // transition's own Pending signal would re-register that signal's observers on the
    // transition forever and it could never settle. Suppress() cuts the inheritance at the
    // detachment point; inside an async method the write stays local to that method's flow.
    internal static void Suppress() => WavefrontFlow.Current.Value = null;
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
    private readonly Transition? prior;
    private readonly bool taggedAmbientFlow;
    // Per reached reaction, the highest invalidation generation a tagged Stale registered: a
    // commit reflects the wavefront's writes to that reaction exactly when its token is >= this
    // threshold (see IStabilizationListener).
    private readonly Dictionary<ReactionBase, int> outstanding = new();
    private readonly Dictionary<ReactionBase, Exception> faults = new();
    private readonly TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PendingPublisher pendingPublisher;
    private volatile bool isPending;
    private bool isSealed;

    // tagAmbientFlow: BeginTransition tags the caller's flow here; a ReactiveAction run passes
    // false and tags its detached body flow itself, because the caller's flow never carries the
    // writes.
    internal Transition(Context context, bool tagAmbientFlow = true)
    {
        pendingPublisher = new(context, () => isPending, "Transition.Pending");
        taggedAmbientFlow = tagAmbientFlow;
        if (tagAmbientFlow)
        {
            prior = TransitionFlow.Current;
            TransitionFlow.Current = this;
        }
    }

    /// <summary>Snapshot: is any reached reaction still awaiting its clean commit?</summary>
    public bool IsPending => isPending;

    /// <summary>
    /// The reactive projection of <see cref="IsPending"/> -- an ordinary graph node, so
    /// spinners and disabled-states are just reactions on it. Published from a detached runtime
    /// flow; it converges on the snapshot but individual flips can lag it.
    /// </summary>
    public IStateGetR<bool> Pending => pendingPublisher.Signal;

    /// <summary>
    /// Completes when the scope has been disposed (sealing the wavefront) AND every reached
    /// reaction has committed clean; faults with an AggregateException when any reached
    /// reaction's update threw instead.
    /// </summary>
    public Task Settled => settled.Task;

    /// <summary>Seals the wavefront and restores the prior ambient transition tag.</summary>
    public void Dispose()
    {
        if (taggedAmbientFlow)
        {
            TransitionFlow.Current = prior;
        }
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
        // A fault can beat the registration the same way a commit can (a zero-debounce update
        // faulting before the listener is added); the recorded fault token recovers it, gated
        // by the same threshold as live fault notifications.
        var fault = reaction.LastStabilizationFault;
        if (fault != null)
        {
            OnFaultedCore(reaction, fault.Token, fault.Exception);
        }
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

    void IStabilizationListener.OnStabilizationFaulted(SignalHandlR node, int token, Exception exception)
    {
        if (node is ReactionBase reaction)
        {
            OnFaultedCore(reaction, token, exception);
        }
    }

    private void OnFaultedCore(ReactionBase reaction, int token, Exception exception)
    {
        bool remove;
        var completed = false;
        lock (gate)
        {
            // Same threshold gate as the clean-commit path: a fault whose update ran against an
            // OLDER generation than this wavefront's registration belongs to a superseded
            // trigger -- the update our own Stale scheduled is still coming and may commit.
            remove = outstanding.TryGetValue(reaction, out var threshold) && token >= threshold;
            if (remove)
            {
                outstanding.Remove(reaction);
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

    private void SchedulePendingPublish() => pendingPublisher.Publish();
}
