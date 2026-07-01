namespace MemoizR;

/// <summary>
/// The actor engine's signal (experimental, ADR 0006): the writable leaf of an actor-engine
/// graph. A <see cref="Set"/> is one actor turn -- value publish plus the whole synchronous
/// invalidation cascade -- so it is atomic with respect to every other piece of graph
/// bookkeeping by construction. Deliberately implements none of the lock-based engine's
/// interfaces: the two engines must not be mixed in one graph, and keeping the types apart
/// makes the mistake impossible to compile.
/// </summary>
public sealed class ActorSignal<T> : ActorValueNode<T>
{
    internal ActorSignal(T value, GraphActor actor)
        : base(value, actor, CacheState.CacheClean)
    {
    }

    public Task Set(T value)
    {
        // The actor engine's equivalent of the lock engine's exclusive-inside-upgradeable
        // rejection: a Set from inside a computation is a feedback loop -- it would invalidate
        // the very evaluation in progress, dooming its commit by design. Detected on the flow,
        // before paying for a turn (MZR003 reports the same mistake at build time).
        if (ActorFlow.Frame.Value != null)
        {
            throw new InvalidOperationException(
                "Set was called from inside a reactive computation. A computation must not write signals " +
                "of its own graph: the write invalidates the evaluation in progress. Return the value instead, " +
                "or schedule the write outside the computation.");
        }

        return Actor.Run(() =>
        {
            if (Equals(Value, value))
            {
                // The value did not change: nothing derived from this signal can have become
                // stale, so do not notify -- the lock engine's Signal.Set rule. (Propagating
                // CacheCheck here bumped observer generations and refused their in-flight
                // commits: under an equal-value write storm, memos could retry indefinitely
                // against reads that were in fact still valid.) The signal's own generation is
                // untouched for the same reason: captured (signal, generation) pairs must keep
                // matching.
                return;
            }

            // The generation bump is what protects evaluations this signal is not yet wired to
            // (their observer link lands only at their commit): their captured pair goes stale
            // and their commit parks Dirty instead of caching a value that predates this write.
            Generation++;
            Value = value;
            PropagateToObservers(CacheState.CacheDirty);
        });
    }

    public Task<T> Get()
    {
        var frame = ActorFlow.Frame.Value;
        if (frame == null)
        {
            // Untracked read: signals are always current; the box read is a complete value.
            return Task.FromResult(Value);
        }

        ActorFlowGuards.RejectCrossActorRead(frame, this);

        // Tracked read: recording the dependency is bookkeeping, so it is a turn -- which also
        // linearizes the returned value against any concurrent Set's cascade, and pins the
        // (source, generation) pair to the very turn the value was served in.
        return Actor.Run(() =>
        {
            frame.Captured.Add((this, Generation));
            return Value;
        });
    }
}
