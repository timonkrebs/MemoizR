namespace MemoizR;

// The stamped read surface for a distributed sync layer (issue #39): GetWithEvidence returns
// the value together with the full causality evidence of the same publication -- one atomic
// pair, so the evidence describes exactly the returned value (never a neighbouring write's) and
// carries its verifiability: a consumer must be able to tell an honest "depends on no tracked
// signals" empty stamp from a poisoned evaluation's "no claim possible", or it could accept a
// value whose evidence was explicitly withdrawn. The concrete node types additionally offer a
// GetWithStamp convenience that projects just the (value, stamp) pair. Implemented by every
// value node (signals, memos, the structured-concurrency nodes); reactions have no value and
// expose only their Evidence.
public interface IStampedGetR<T> : IStateGetR<T>
{
    Task<(T Value, StampEvidence Evidence)> GetWithEvidence();

    StampEvidence Evidence { get; }
}
