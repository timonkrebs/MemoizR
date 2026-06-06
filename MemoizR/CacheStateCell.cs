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
    private int generation;

    // Lock-free read for the Get fast path and the state-machine branches.
    public CacheState State => state;

    // Snapshot the generation without changing state, for a caller that may commit Clean later
    // without going through BeginEvaluation (the CacheCheck path that resolves without recompute).
    public int Generation
    {
        get { lock (gate) { return generation; } }
    }

    // Begin an evaluation: mark Evaluating and return the generation to commit against.
    public int BeginEvaluation()
    {
        lock (gate)
        {
            state = CacheState.Evaluating;
            return generation;
        }
    }

    // Escalate dirtiness (Stale). Bumps the generation so an in-flight evaluation cannot commit
    // Clean over it. Returns false if the node was already at least this dirty (no change).
    public bool Invalidate(CacheState newState)
    {
        lock (gate)
        {
            if (newState <= state) return false;
            state = newState;
            generation++;
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
