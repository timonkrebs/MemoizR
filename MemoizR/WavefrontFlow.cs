namespace MemoizR;

// The ambient write-wavefront observer (ADR 0007): set on a writing flow by MemoizR.Reactive's
// transition scopes and action runs, held as an opaque object so the core stays ignorant of the
// Reactive types (Reactive reads it back with a cast). Its presence changes ONE core behavior:
// an invalidation cascade normally prunes at a node that is already at least as dirty -- its
// observers were notified when it first reached that state -- but a pruned cascade never reaches
// the downstream reactions, so a tagged write through an already-dirty node would be invisible
// to the wavefront observer: it could settle before the write's effects applied, and a repeated
// write could not raise the completion thresholds of already-registered reactions. With an
// observer active the cascade therefore always propagates. The cost is re-walking
// already-notified subgraphs -- bounded by the DAG's path count, small in UI-shaped graphs --
// and only for tagged writes; untagged writes keep the pruned cascade.
internal static class WavefrontFlow
{
    internal static readonly AsyncLocal<object?> Current = new();

    internal static bool IsActive => Current.Value != null;
}
