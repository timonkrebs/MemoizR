namespace MemoizR;

/// <summary>Behavioral options for a <see cref="MemoFactory"/>.</summary>
[Flags]
public enum MemoFactoryOptions
{
    None = 0,

    /// <summary>
    /// Validate at node creation that the value type handed to a signal/memo/concurrent node is
    /// Sendable (deeply immutable or thread-safe -- see <see cref="SendableChecker"/>), throwing
    /// otherwise. The runtime analog of Swift's strict concurrency checking (issue #36): MemoizR
    /// publishes value references tear-free across flows, but only a Sendable type makes the
    /// object behind the reference safe to share. Off by default for compatibility.
    /// </summary>
    StrictSendableChecks = 1 << 0,
}
