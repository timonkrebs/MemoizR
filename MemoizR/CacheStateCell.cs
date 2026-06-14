namespace MemoizR;

// Holds a memo node's CacheState and guards its transitions against the cross-flow lost-update
// race: because the per-flow ContextLock does not serialize a Set's Stale (on one flow) against a
// concurrent Get's recompute (on another), a recompute could otherwise commit CacheClean over a
// Dirty mark that arrived while it was evaluating, leaving the memo cached-stale.
//
// A monotonic generation is bumped on every invalidation. An evaluation snapshots the generation
// when it begins and only commits Clean if the generation is unchanged at the end; otherwise the
// node is left dirty for the next Get to recompute. The current state is exposed through a plain
// volatile read so the lock-free Get fast path stays lock-free; only the writers take the gate.
internal sealed class CacheStateCell(CacheState initial)
{
    private readonly Lock gate = new();
    private volatile CacheState state = initial;
    private volatile int generation;

    // Lock-free read for the Get fast path and the state-machine branches.
    public CacheState State => state;

    // Snapshot the generation without changing state, for a caller that may commit Clean later
    // without going through BeginEvaluation (the CacheCheck path that resolves without recompute).
    // Lock-free: a volatile read can only be STALE-LOW (it never sees an unwritten bump), and a
    // stale-low token makes the later TryCommitClean -- which compares under the gate -- REFUSE,
    // which is the same conservative outcome as a racing invalidation. It can never let a commit
    // through that the locked read would have refused.
    public int Generation => generation;

    // Begin an evaluation: mark Evaluating and return the generation to commit against.
    public int BeginEvaluation()
    {
        lock (gate)
        {
            state = CacheState.Evaluating;
            return generation;
        }
    }

    // Escalate dirtiness (Stale). ALWAYS bumps the generation -- even when the state is already at
    // least this dirty -- so an in-flight evaluation can never commit Clean over an invalidation
    // it has not observed. (A suppressed bump would let a node sitting in CacheCheck commit Clean
    // over a Stale that arrived mid-parent-check and cache a stale value with no recovery, because
    // the cascade also stops at already-dirty nodes.) Returns false if the state did not change
    // (the node was already at least this dirty); callers use that to skip re-propagating to
    // observers, which were already notified when this node first reached that state -- an
    // observer that commits Clean inside the race window is re-notified by the failed commit
    // instead (see SignalHandlR.CommitCleanOrRenotifyAsync).
    public bool Invalidate(CacheState newState)
    {
        lock (gate)
        {
            generation++;
            if (newState <= state) return false;
            state = newState;
            return true;
        }
    }

    // Unconditionally move to a non-clean state (constructor Dirty, catch -> CacheCheck). Counts
    // as an invalidation against any in-flight evaluation.
    public void Force(CacheState newState)
    {
        lock (gate)
        {
            state = newState;
            generation++;
        }
    }

    // Escalate dirtiness because a parent we depend on recomputed (the diamond down-link). This
    // does NOT bump the generation: when it fires during this node's own (same-flow) evaluation --
    // the node is reading that very parent -- it must be absorbed, not treated as a concurrent
    // invalidation, otherwise the node would needlessly recompute again. A later Get still sees
    // the dirty state and recomputes.
    public void InvalidateFromParent(CacheState newState)
    {
        lock (gate)
        {
            if (newState > state) state = newState;
        }
    }

    // Commit Clean only if no invalidation happened since `token` was snapshotted. Returns false
    // if a concurrent invalidation won, leaving the node dirty for the next Get to recompute.
    public bool TryCommitClean(int token)
    {
        lock (gate)
        {
            if (generation != token) return false;
            state = CacheState.CacheClean;
            return true;
        }
    }
}
