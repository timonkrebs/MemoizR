using Nito.AsyncEx;

namespace MemoizR;

public abstract class SignalHandlR : IMemoHandlR
{
    private Lock Lock { get; } = new();
    internal IMemoHandlR[] Sources { get; set; } = []; // sources in reference order, not deduplicated (up links)
    internal WeakReference<IMemoizR>[] Observers { get; set; } = []; // nodes that have us as sources (down links)

    internal Context Context;

    protected AsyncLock mutex = new();

    IMemoHandlR[] IMemoHandlR.Sources
    {
        get => Sources;
        set
        {
            lock (Lock)
            {
                Sources = value;
            }
        }
    }
    WeakReference<IMemoizR>[] IMemoHandlR.Observers
    {
        get => Observers;
        set
        {
            lock (Lock)
            {
                Observers = value;
            }
        }
    }

    // Cache-state cell for every node type that participates in invalidation (memos, the
    // concurrent nodes, reactions); see CacheStateCell for the generation-guard protocol. Plain
    // signals never touch it (their writes are Sets under the ContextLock; they have no cached
    // recompute state), they just carry the tiny unused cell so the protocol lives in one place.
    internal readonly CacheStateCell stateCell = new(CacheState.CacheClean);

    public string Label { get; init; } = "Label";

    internal SignalHandlR(Context context)
    {
        this.Context = context;
    }

    // Atomic add-if-absent on our observer down-links. The membership check and the array swap
    // must happen under one monitor: observer mutations arrive from three different lock domains
    // (capture under Context.Lock, rewiring under a flow's ContextLock + node mutex, job
    // accumulation under the job Lock), and two unsynchronized read-modify-writes of the same
    // array lose one of the entries -- a silently dropped subscription, i.e. a missed-trigger.
    void IMemoHandlR.AddObserver(IMemoizR observer) => AddObserver(observer);

    internal void AddObserver(IMemoizR observer)
    {
        lock (Lock)
        {
            foreach (var existing in Observers)
            {
                if (existing.TryGetTarget(out var o) && ReferenceEquals(o, observer))
                {
                    return;
                }
            }
            Observers = [.. Observers, new(observer)];
        }
    }

    void IMemoHandlR.RemoveObserver(IMemoizR observer) => RemoveObserver(observer);

    internal void RemoveObserver(IMemoizR observer)
    {
        lock (Lock)
        {
            // Dead weak references are swept opportunistically while we are rebuilding anyway.
            Observers = [.. Observers.Where(x => x.TryGetTarget(out var o) && !ReferenceEquals(o, observer))];
        }
    }

    // Rewire our source up-links and the parents' observer down-links to match the sources
    // captured during the current evaluation (Context.ReactionScope.CurrentGets). Must only be
    // called by IMemoizR nodes, inside their ContextLock-serialized evaluation.
    //
    // Diff-based on purpose: only sources DROPPED by this run lose their down-link to us.
    // The previous strip-everything-then-re-add left a window in which a retained source had no
    // observer entry for us -- a Set landing there notified nobody and never bumped our
    // generation, so the value committed at the end of the evaluation cached stale forever.
    internal void UpdateSourceAndObserverLinks()
    {
        var self = (IMemoizR)this;
        // Resolve the scope once: every Context.ReactionScope access takes the context-wide lock
        // plus a dictionary probe, and this method reads it many times per recompute.
        var scope = Context.ReactionScope;

        IMemoHandlR[] newSources;
        if (scope.CurrentGets.Length > 0)
        {
            newSources = Sources.Length > 0 && scope.CurrentGetsIndex > 0
                ? [.. Sources.AsSpan(0, scope.CurrentGetsIndex), .. scope.CurrentGets]
                : scope.CurrentGets;
        }
        else if (Sources.Length > 0 && scope.CurrentGetsIndex < Sources.Length)
        {
            newSources = [.. Sources.AsSpan(0, scope.CurrentGetsIndex)];
        }
        else
        {
            return; // dependency set unchanged
        }

        foreach (var old in Sources)
        {
            if (!newSources.Contains(old))
            {
                old.RemoveObserver(self);
            }
        }
        Sources = newSources;
        foreach (var source in newSources)
        {
            // Usually a no-op: capture-time eager subscription already wired the link.
            source.AddObserver(self);
        }
    }

    internal void RemoveParentObservers(int index)
    {
        var self = (IMemoizR)this;
        for (var i = index; i < Sources.Length; i++)
        {
            Sources[i].RemoveObserver(self);
        }
    }

    // The diamond down-link: after this node recomputed to a changed value, mark our observers
    // dirty so they re-evaluate. Goes through the IMemoizR.State setter (InvalidateFromParent),
    // which absorbs the mark during the observer's own same-flow evaluation -- the observer is
    // reading this very node -- instead of bumping its generation. Iterating an empty Observers
    // array is a no-op, so the caller only needs its value-changed guard.
    internal void MarkObserversDirty()
    {
        foreach (var observer in Observers)
        {
            if (observer.TryGetTarget(out var o))
            {
                o.State = CacheState.CacheDirty;
            }
        }
    }

    // Escalate this node's dirtiness (a Stale) and, if the state escalated, propagate CacheCheck
    // to our observers. The generation is bumped even when the state was already at least this
    // dirty (see CacheStateCell.Invalidate); propagation is skipped then because the observers
    // were already notified when this node first reached that state -- an observer that commits
    // Clean inside the race window is re-notified by CommitCleanOrRenotifyAsync instead.
    // Non-async on purpose: the suppressed case is the common one under write storms and should
    // not pay for an async state machine.
    internal Task InvalidateAndPropagateAsync(CacheState state)
    {
        if (!stateCell.Invalidate(state))
        {
            return Task.CompletedTask;
        }

        return PropagateStaleToObserversAsync();
    }

    internal async Task PropagateStaleToObserversAsync(CacheState state = CacheState.CacheCheck)
    {
        foreach (var observer in Observers)
        {
            if (observer.TryGetTarget(out var o))
            {
                await o.Stale(state);
            }
        }
    }

    // Commit Clean against the snapshotted generation. If the commit is refused because an
    // invalidation landed mid-evaluation, this node stays dirty for its next Get/update -- but an
    // observer may have committed Clean against our pre-invalidation value in the same window
    // (the invalidation cascade stops at already-dirty nodes, so it can have missed the observer
    // entirely), so re-notify the observers; for a reaction observer this also re-schedules its
    // debounced update. Without this, a node whose commit lost the race could leave a descendant
    // cached-stale with nothing ever re-dirtying it.
    // Non-async, with a lock-free pre-check: if the state is already Clean, either this very
    // token's early commit succeeded (every invalidation escalates the state away from Clean, so
    // Clean here implies an unchanged generation) or a newer evaluation committed -- in both
    // cases there is nothing to do, and the common recompute path skips the gate entirely.
    internal Task CommitCleanOrRenotifyAsync(int token)
    {
        if (stateCell.State == CacheState.CacheClean || stateCell.TryCommitClean(token))
        {
            return Task.CompletedTask;
        }

        return PropagateStaleToObserversAsync();
    }
}

public abstract class MemoHandlR<T> : SignalHandlR
{
    // Value is read on the lock-free Get fast path while another flow may be writing it under the
    // ContextLock. A generic T can be neither marked `volatile` nor read with Volatile.Read (the
    // generic overload is class-constrained), and a large struct T can tear under a concurrent
    // write. So the value is published through an immutable box held in a single volatile
    // reference: a write swaps in a fully-constructed box (an atomic reference store with release
    // semantics), and a read takes the reference once and returns its readonly field -- always a
    // complete, untorn value. Every Update writes Value before setting State = CacheClean (a
    // volatile release) and the fast path reads State (a volatile acquire) before Value, so a
    // reader that observes CacheClean is guaranteed to see the box of that-or-a-newer clean
    // generation. The read is therefore a linearizable snapshot, not an eventually-consistent one.
    private volatile ValueBox valueBox = new(default!);

    internal T Value
    {
        get => valueBox.Value;
        set => valueBox = new ValueBox(value);
    }

    internal MemoHandlR(Context context) : base(context)
    {
    }

    private sealed class ValueBox(T value)
    {
        public readonly T Value = value;
    }
}
