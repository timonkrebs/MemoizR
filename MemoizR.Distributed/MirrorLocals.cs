using System.Runtime.CompilerServices;

namespace MemoizR.Distributed;

// The identity set of local signals that back remote mirrors. A mirror's local stamps are
// PEER-LOCAL (its own context's ids and triggers); the foreign evidence travels beside the
// graph in RemoteSignal.Publication until wire-format v3 splices it. Re-exporting such a
// signal would advertise the local stamps as if they were evidence: two re-exported mirrors
// of the same origin host carry disjoint local stamps, so a downstream barrier would find any
// pair of them vacuously consistent and render torn origin snapshots. Export refuses them by
// identity (a weak table, so mirrors stay collectible) -- the type system cannot express the
// distinction, because the mirror's local is deliberately an ordinary eager signal.
internal static class MirrorLocals
{
    private static readonly ConditionalWeakTable<SignalHandlR, object> Registry = new();
    private static readonly object Present = new();

    internal static void Register(SignalHandlR local) => Registry.AddOrUpdate(local, Present);

    internal static bool IsMirrorLocal(SignalHandlR node) => Registry.TryGetValue(node, out _);
}
