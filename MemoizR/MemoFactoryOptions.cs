namespace MemoizR;

/// <summary>Behavioral options for a <see cref="MemoFactory"/>.</summary>
[Flags]
public enum MemoFactoryOptions
{
    None = 0,

    /// <summary>
    /// Validate at node creation that the value type handed to a signal/memo/concurrent node is
    /// Sendable (deeply immutable or thread-safe -- see <see cref="SendableChecker"/>), throwing
    /// otherwise. The runtime analog of Swift's strict concurrency checking (issue #36).
    /// Since the Swift-6-parity step (issue #145 part A4) this is the DEFAULT -- the flag is
    /// kept for source compatibility and as an explicit statement of intent; opt out with
    /// <see cref="DisableSendableChecks"/> during migration.
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

    /// <summary>
    /// Additionally validate, on every SIGNAL write (and the creation value) -- memo outputs
    /// are the computation's own doing and publish unchecked -- that the WRITTEN INSTANCE'S
    /// RUNTIME TYPE is Sendable -- closing the subclass-smuggling hole the
    /// creation-time check cannot see (a mutable subclass behind an upcast; see MZR006 and ADR
    /// 0003). Costs one cached type probe per Set, so it is a separate opt-in on top of
    /// <see cref="StrictSendableChecks"/>. Ignored when <see cref="DisableSendableChecks"/>
    /// is set: the migration escape hatch switches off ALL Sendable validation.
    /// </summary>
    ValidateWrittenValues = 1 << 2,

    /// <summary>
    /// Opt OUT of the (default) creation-time Sendable checks -- the migration escape hatch,
    /// the analog of staying on Swift 5's language mode. Values of non-Sendable types are then
    /// shared across flows unchecked, exactly as before the Swift-6-parity step. Takes
    /// precedence over <see cref="StrictSendableChecks"/> and
    /// <see cref="ValidateWrittenValues"/>: opting out switches off ALL Sendable validation.
    /// The analyzers honor a VISIBLE opt-out too: MZR001/MZR006 skip creations on a factory
    /// constructed with this flag in a compile-time-constant options argument in sight (an
    /// inline receiver, or the same-file initializer of a local/readonly field/get-only
    /// property that is never reassigned); factories the build cannot see behind stay checked.
    /// </summary>
    DisableSendableChecks = 1 << 3,
}
