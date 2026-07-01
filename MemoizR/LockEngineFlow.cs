namespace MemoizR;

// Flow-ambient marker of the lock-engine computation currently evaluating on this flow --
// context-AGNOSTIC, where the per-context scope machinery cannot be: the actor engine's
// cross-engine read guard must detect a capturing computation of ANY context, not just the
// actor node's own (each Context keeps its flow state in its own AsyncLocals, so there is
// nothing per-context code could enumerate). Set and restored exactly where a computation
// installs itself as scope.CurrentReaction (MemoBase, ReactionBase, ConcurrentRace,
// StructuredResultsJob) -- the only places CurrentReaction becomes non-null. Untrack needs no
// mirroring: the guard re-checks CurrentReaction through the ambient context, and Untrack nulls
// it. Costs one AsyncLocal set/restore pair per EVALUATION (which already acquires async locks
// and resolves scopes); the Get fast paths never touch it.
internal static class LockEngineFlow
{
    internal static readonly AsyncLocal<Context?> EvaluatingContext = new();
}
