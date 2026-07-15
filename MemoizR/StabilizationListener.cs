namespace MemoizR;

// The internal "committed Clean" half of the observer protocol (ADR 0007 phase 1). Observers
// hear about invalidations (Stale) but nothing hears about a node committing Clean again --
// which is exactly the edge transitions and pending indicators anchor to. Kept internal and
// separate from IMemoizR on purpose: observers are graph structure (they receive semantics --
// re-check, recompute), listeners are pure telemetry and must never influence propagation.
//
// Registration protocol for consumers (the reason the token exists): take the invalidation's
// post-bump generation from CacheStateCell.Invalidate(state, out generationAfter), register the
// listener, then check node.stateCell.LastCleanCommitToken >= generationAfter to recover a
// commit that raced the registration window. From then on, a notification with
// token >= generationAfter means the node's committed state reflects that invalidation --
// TryCommitClean only succeeds against an unchanged generation, so a commit whose evaluation
// predates the invalidation can never carry a satisfying token, no matter how late its
// notification is delivered.
internal interface IStabilizationListener
{
    // Called after `node` committed Clean against `token`. Runs INSIDE the committing
    // evaluation (node mutex + flow ContextLock held), so implementations must be fast,
    // non-blocking, must not re-enter the graph (no Get/Set/Invalidate), and must complete any
    // TaskCompletionSource with RunContinuationsAsynchronously. Calls can arrive out of order
    // across flows and more than once per logical stabilization -- treat the token as level
    // information (threshold comparison), never as an edge. A dispose-time release arrives as
    // token == int.MaxValue: the node will never commit again, so every threshold is moot.
    void OnStabilized(SignalHandlR node, int token);

    // Called when an update of `node` faulted instead of committing (today: a reaction whose
    // Execute threw -- the pull path surfaces memo faults to the Get caller instead). The node
    // stays dirty and nothing is rescheduled until its next invalidation, so a waiter must not
    // keep waiting for a commit that will never come. Same execution constraints as
    // OnStabilized.
    void OnStabilizationFaulted(SignalHandlR node, Exception exception);
}
