namespace MemoizR;

// Flow-side ambient state for the actor engine. Set and read ONLY on user flows (never inside
// turns: AsyncLocal mutations made on the actor loop could never reach the flows anyway).
internal static class ActorFlow
{
    // The dependency-capture frame of the innermost actor-memo evaluation on this flow.
    internal static readonly AsyncLocal<EvaluationFrame?> Frame = new();

    // The frame, treating an EXPIRED one as absent. ExecutionContext capture (Task.Run, timer
    // callbacks, thread-pool work items started inside a computation) carries the AsyncLocal
    // into work that may run long after the evaluation committed -- the documented "schedule
    // the write outside the evaluation" escape does exactly that. Such work is no longer
    // inside any evaluation, so its stale frame must not track reads or reject writes.
    internal static EvaluationFrame? CurrentFrame => Frame.Value is { IsExpired: false } frame ? frame : null;
}

// Flow-side rejections shared by the read entry points.
internal static class ActorFlowGuards
{
    // The reverse direction of the engine-separation rule: a lock-engine computation that reads
    // an actor node gets the value with NO dependency registered in either graph -- the actor
    // engine tracks through ActorFlow frames, which lock computations do not carry, and the lock
    // engine tracks through IMemoHandlR, which actor nodes deliberately do not implement. The
    // computation would cache a value that no later actor-side Set can ever invalidate:
    // permanent staleness, so the read is rejected on the flow. The evaluating computation is
    // found through the context-agnostic LockEngineFlow ambient (a computation of ANY context
    // on this flow is the same staleness, so the node's own context must not be the only one
    // checked), and its capture state re-checked through that context -- which keeps
    // Context.Untrack working as the escape hatch: an untracked snapshot read is exactly what
    // it asks for (it clears CurrentReaction, so the capture predicate goes quiet).
    public static void RejectUntrackedReadInsideLockComputation()
    {
        if (LockEngineFlow.EvaluatingContext.Value is { IsComputationCapturing: true })
        {
            throw new InvalidOperationException(
                "An actor-engine node was read from inside a lock-engine computation. The two engines " +
                "cannot track dependencies across each other, so this read would never re-trigger the " +
                "computation -- it would cache a permanently stale value. Keep a graph on one engine end " +
                "to end, or wrap the read in Untrack for a deliberately untracked snapshot.");
        }
    }

    // A tracked read of a node that lives on a DIFFERENT GraphActor would capture a foreign
    // source: the reader's commit turn would then rewire that source's observer list while
    // running on the reader's actor -- mutating actor-confined state from the wrong actor (the
    // Debug isolation assertion trips deep inside the commit; a Release build would race the
    // foreign actor's own turns). Each Context has one actor, so this rejects the cross-context
    // dependency at the read, on the flow, with an error that names the actual mistake.
    public static void RejectCrossActorRead(EvaluationFrame? frame, ActorNodeBase node)
    {
        if (frame != null && !ReferenceEquals(frame.Owner.Actor, node.Actor))
        {
            throw new InvalidOperationException(
                "An actor computation read an actor node that belongs to a different context. " +
                "Actor-engine nodes can only depend on nodes of the same context: a cross-context " +
                "link would mutate another actor's graph. Create nodes that must depend on each " +
                "other from one factory, or share a context by key (new MemoFactory(\"sharedKey\")).");
        }
    }
}

// One evaluation's dependency capture, doubling as a link in the EVALUATION CHAIN used for cycle
// detection. Created in ComputeAndCommit (owner = the evaluating memo, parent = the chain the
// read arrived on), installed on the evaluating flow, discarded after the Commit turn.
//
// Capture: every tracked read records the source TOGETHER WITH the source's generation, in the
// same turn that serves the value: at commit, a pair whose source generation moved proves the
// computation consumed a value that is no longer (or never was) confirmed -- the late-wiring
// guard, see ActorMemo.Commit. The list is mutated only inside turns, so concurrent tracked
// reads inside one computation (e.g. Task.WhenAll over two Gets) are serialized by the actor.
//
// Cycles: a read is a cycle exactly when it re-enters a node whose own in-flight evaluation the
// read is nested inside -- i.e. the node appears in the reader's chain. Bare flow identity
// cannot make that distinction: two unawaited sibling Gets of one memo issued by the same
// computation share the flow but are NOT a cycle (the first evaluation completes fine; the
// second must wait), while a genuine self-reach through any number of nested reads or parent
// scans is. Scans extend the chain with a link-only frame (owner = the scanning node) so a
// dependency cycle closed through a CacheCheck parent scan is caught too.
internal sealed class EvaluationFrame
{
    public readonly List<(ActorNodeBase Source, int GenerationAtRead)> Captured = [];

    public readonly ActorNodeBase Owner;
    public readonly EvaluationFrame? Parent;

    // Set once when the owning evaluation ends (commit or fault). The frame object is shared by
    // every ExecutionContext snapshot that captured it, so this one write is visible to deferred
    // work carrying a stale copy of the flow's context -- volatile because those reads happen on
    // arbitrary threads (see ActorFlow.CurrentFrame).
    private volatile bool expired;

    public EvaluationFrame(ActorNodeBase owner, EvaluationFrame? parent)
    {
        Owner = owner;
        Parent = parent;
    }

    public bool IsExpired => expired;

    public void Expire() => expired = true;

    public bool Contains(ActorNodeBase node)
    {
        for (var frame = this; frame != null; frame = frame.Parent)
        {
            if (ReferenceEquals(frame.Owner, node))
            {
                return true;
            }
        }

        return false;
    }
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

    // The owning context: actor nodes are citizens of exactly one context's graph (its actor
    // serializes their bookkeeping), and the read guards use it to detect a lock-engine
    // computation of the SAME context capturing on the current flow.
    internal readonly Context Context;

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

    internal ActorNodeBase(Context context, CacheState initialState)
    {
        Context = context;
        Actor = context.GraphActor;
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
    // of IMemoizR.UpdateIfNecessary). The chain carries the scanning evaluation's ancestry for
    // cycle detection only -- nothing is captured. Signals are always up to date.
    internal virtual Task EnsureCleanAsync(EvaluationFrame? chain)
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

    internal ActorValueNode(T initialValue, Context context, CacheState initialState)
        : base(context, initialState)
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
