namespace MemoizR;

// The stamped read surface for a distributed sync layer (issue #39): GetWithStamp returns the
// value together with the causality stamp of the same publication -- one atomic pair, so the
// stamp describes exactly the returned value (never a neighbouring write's). Implemented by
// every value node (signals, memos, the structured-concurrency nodes); reactions have no value
// and expose only their Stamp.
public interface IStampedGetR<T> : IStateGetR<T>
{
    Task<(T Value, CausalityStamp Stamp)> GetWithStamp();

    CausalityStamp Stamp { get; }
}
