namespace MemoizR;

// Flow-side ambient state for the actor engine. Set and read ONLY on user flows (never inside
// turns: AsyncLocal mutations made on the actor loop could never reach the flows anyway).
internal static class ActorFlow
{
    private static long nextId;

    // Flow identity for cycle detection: the flow that began an evaluation re-entering the same
    // node is a cycle; a different flow is a legitimate concurrent reader that waits.
    private static readonly AsyncLocal<long> FlowId = new();

    // The dependency-capture frame of the innermost actor-memo evaluation on this flow.
    internal static readonly AsyncLocal<EvaluationFrame?> Frame = new();

    public static long GetOrMintId()
    {
        if (FlowId.Value == 0)
        {
            FlowId.Value = Interlocked.Increment(ref nextId);
        }

        return FlowId.Value;
    }
}

// One evaluation's dependency capture. Created in the Begin turn, installed on the evaluating
// flow, discarded after the Commit turn. Every tracked read records the source TOGETHER WITH the
// source's generation, in the same turn that serves the value: at commit, a pair whose source
// generation moved proves the computation consumed a value that is no longer (or never was)
// confirmed -- the late-wiring guard, see ActorMemo.Commit. The list is mutated only inside
// turns, so concurrent tracked reads inside one computation (e.g. Task.WhenAll over two Gets)
// are serialized by the actor, not by a lock.
internal sealed class EvaluationFrame
{
    public readonly List<(ActorNodeBase Source, int GenerationAtRead)> Captured = [];
}

/// <summary>
/// Shared bookkeeping of the experimental actor-engine nodes (ADR 0006). Everything in this
/// class is <b>actor-confined</b>: plain fields, in-place mutations, no monitors, no volatile --
/// touched exclusively inside <see cref="GraphActor"/> turns, which is the whole point of the
/// design. The single deliberate exception is <see cref="state"/>, which the lock-free Get fast
/// path reads cross-thread and is therefore volatile (its writes still happen only in turns).
/// </summary>
public abstract class ActorNodeBase
{
    internal readonly GraphActor Actor;

    // Up/down links. Sources is swapped wholesale in commit turns (so a Scan may iterate a
    // snapshot off-actor); Observers is mutated in place -- safe here, unlike the lock-based
    // engine, precisely because only turns touch it. Observers are weak so dropped nodes are
    // collected and pruned on the next cascade walk.
    internal ActorNodeBase[] Sources = [];
    internal readonly List<WeakReference<ActorNodeBase>> Observers = [];

    // The cache-state machine and generation guard, ported from CacheStateCell: the guard is
    // inherent to lazy memoization (an evaluation spans multiple turns, so an invalidation can
    // land between Begin and Commit) -- the actor removes the locks, not the optimism.
    private volatile CacheState state;
    internal int Generation;

    internal CacheState State
    {
        get => state;
        set
        {
            AssertOnActor();
            state = value;
        }
    }

    internal ActorNodeBase(GraphActor actor, CacheState initialState)
    {
        Actor = actor;
        state = initialState;
    }

    // Escalate dirtiness (a Stale). ALWAYS bumps the generation -- even when the state was
    // already at least this dirty -- so an in-flight evaluation can never commit Clean over an
    // invalidation it has not observed (the suppressed-Stale rule, concurrency.md §6.3).
    // Propagation recurses only on escalation, which is what terminates diamond-heavy cascades.
    internal void Invalidate(CacheState newState)
    {
        AssertOnActor();
        Generation++;
        if (newState <= State)
        {
            return;
        }

        State = newState;
        PropagateToObservers(CacheState.CacheCheck);
    }

    internal void PropagateToObservers(CacheState newState)
    {
        AssertOnActor();
        for (var i = Observers.Count - 1; i >= 0; i--)
        {
            if (Observers[i].TryGetTarget(out var observer))
            {
                observer.Invalidate(newState);
            }
            else
            {
                Observers.RemoveAt(i);
            }
        }
    }

    // The diamond down-link: a parent that recomputed to a changed value marks its observers
    // dirty WITHOUT bumping their generation -- during the observer's own evaluation (it is
    // reading that very parent) the mark must be absorbed, not treated as a concurrent
    // invalidation (concurrency.md §6.4).
    internal void MarkDirtyFromParent()
    {
        AssertOnActor();
        if (CacheState.CacheDirty > State)
        {
            State = CacheState.CacheDirty;
        }
    }

    internal void AddObserver(ActorNodeBase observer)
    {
        AssertOnActor();
        Observers.Add(new(observer));
    }

    internal void RemoveObserver(ActorNodeBase observer)
    {
        AssertOnActor();
        Observers.RemoveAll(w => !w.TryGetTarget(out var o) || ReferenceEquals(o, observer));
    }

    // Bring this node up to date without dependency capture (the parent-scan path; the analog
    // of IMemoizR.UpdateIfNecessary). Signals are always up to date.
    internal virtual Task EnsureCleanAsync()
    {
        return Task.CompletedTask;
    }

    internal void MarkObserversDirtyFromParent()
    {
        AssertOnActor();
        for (var i = Observers.Count - 1; i >= 0; i--)
        {
            if (Observers[i].TryGetTarget(out var observer))
            {
                observer.MarkDirtyFromParent();
            }
            else
            {
                Observers.RemoveAt(i);
            }
        }
    }

    // Dynamic isolation check (layer 3 applied to layer 5): every mutator above is
    // actor-confined by design; in Debug builds, every test run proves it on every operation.
    [System.Diagnostics.Conditional("DEBUG")]
    private protected void AssertOnActor()
    {
        Actor.AssertIsolated();
    }
}

/// <summary>
/// An actor-engine node carrying a value. The value is published exactly like the lock-based
/// engine's (ADR 0001 rule 4): an immutable box behind a volatile reference, written only in
/// turns, with every commit writing the box BEFORE the volatile state -- so the lock-free fast
/// path that reads state (acquire) then the box observes a complete value of that-or-a-newer
/// generation. These two volatile fields are the actor engine's entire memory-model surface.
/// </summary>
public abstract class ActorValueNode<T> : ActorNodeBase
{
    private volatile Box box;

    internal ActorValueNode(T initialValue, GraphActor actor, CacheState initialState)
        : base(actor, initialState)
    {
        box = new(initialValue);
    }

    internal T Value
    {
        get => box.Value;
        set
        {
            AssertOnActor();
            box = new(value);
        }
    }

    private sealed class Box(T value)
    {
        public readonly T Value = value;
    }
}
