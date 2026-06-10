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

    internal bool isStartingComponent;

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

    // Rewire our source up-links and the parents' observer down-links to match the sources
    // captured during the current evaluation (Context.ReactionScope.CurrentGets). Must only be
    // called by IMemoizR nodes, inside their ContextLock-serialized evaluation.
    internal void UpdateSourceAndObserverLinks()
    {
        var self = (IMemoizR)this;

        // if the sources have changed, update source & observer links
        if (Context.ReactionScope.CurrentGets.Length > 0)
        {
            // remove all old Sources' .observers links to us
            RemoveParentObservers(Context.ReactionScope.CurrentGetsIndex);
            // update source up links
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
                // Add ourselves to the end of the parent .observers array
                var source = Sources[i];
                source.Observers = !source.Observers.Any()
                    ? [new(self)]
                    : [.. source.Observers, new(self)];
            }
        }
        else if (Sources.Any() && Context.ReactionScope.CurrentGetsIndex < Sources.Length)
        {
            // remove all old Sources' .observers links to us
            RemoveParentObservers(Context.ReactionScope.CurrentGetsIndex);
            Sources = [.. Sources.Take(Context.ReactionScope.CurrentGetsIndex)];
        }
    }

    internal void RemoveParentObservers(int index)
    {
        if (!Sources.Any()) return;
        foreach (var source in Sources.Skip(index))
        {
            source.Observers = [.. source.Observers.Where(x => x.TryGetTarget(out var o) ? !ReferenceEquals(o, this) : false)];
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
    internal async Task InvalidateAndPropagateAsync(CacheState state)
    {
        if (!stateCell.Invalidate(state))
        {
            return;
        }

        await PropagateStaleToObserversAsync();
    }

    internal async Task PropagateStaleToObserversAsync()
    {
        foreach (var observer in Observers)
        {
            if (observer.TryGetTarget(out var o))
            {
                await o.Stale(CacheState.CacheCheck);
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
    internal async Task CommitCleanOrRenotifyAsync(int token)
    {
        if (stateCell.TryCommitClean(token))
        {
            return;
        }

        if (stateCell.State == CacheState.CacheClean)
        {
            // A newer evaluation already committed; nothing is pending and observers are consistent.
            return;
        }

        await PropagateStaleToObserversAsync();
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
