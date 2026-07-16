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
    internal const string ReExportMessage =
        "A remote mirror's local signal cannot be re-exported (nor can a node derived from one): its local stamps do not carry the origin's evidence. Multi-hop bridging needs the wire-v3 evidence splicing planned in issue #148.";

    private static readonly ConditionalWeakTable<SignalHandlR, object> Registry = new();
    private static readonly object Present = new();

    internal static void Register(SignalHandlR local) => Registry.AddOrUpdate(local, Present);

    internal static bool IsMirrorLocal(SignalHandlR node) => Registry.TryGetValue(node, out _);

    // Whether the node is a mirror local or (transitively) depends on one: a memo built over a
    // mirror carries stamps captured from the consumer-local trigger, so re-exporting it is
    // the same unsoundness one hop removed. Walks the CURRENT source wiring -- a lazy memo has
    // none until its first evaluation, which is why the pull re-checks at the wire egress.
    internal static bool TouchesMirrorLocal(SignalHandlR node)
    {
        if (IsMirrorLocal(node))
        {
            return true;
        }

        var visited = new HashSet<SignalHandlR>();
        var pending = new Stack<SignalHandlR>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            foreach (var source in pending.Pop().Sources)
            {
                if (source is SignalHandlR handle && visited.Add(handle))
                {
                    if (IsMirrorLocal(handle))
                    {
                        return true;
                    }
                    pending.Push(handle);
                }
            }
        }
        return false;
    }
}
