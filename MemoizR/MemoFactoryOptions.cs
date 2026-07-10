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

    /// <summary>
    /// Turn off causality-stamp capture (issue #39) for this factory's context: single-process
    /// graphs that never exchange stamps with distributed peers skip the per-Set stamp
    /// construction and the per-recompute capture bookkeeping entirely. With stamps disabled,
    /// <c>GetWithStamp</c> returns the empty stamp and <c>GetWithEvidence</c> returns evidence
    /// flagged <see cref="StampEvidence.Unverifiable"/> -- "no claim can be made", so a
    /// consistency check can never mistake the missing capture for a verified value. The
    /// setting is context-wide: creating a factory for an existing keyed context with a
    /// different stamp setting throws.
    /// </summary>
    DisableCausalityStamps = 1 << 1,
}
