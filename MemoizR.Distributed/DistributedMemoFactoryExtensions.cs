using MemoizR.Reactive;

namespace MemoizR.Distributed;

public static class DistributedMemoFactoryExtensions
{
    /// <summary>
    /// Export a stamped node: whenever its value advances, <paramref name="publishStale"/>
    /// receives the advertisement (write it to your transport); pulls are answered by
    /// <see cref="ExportedNode{T}.PullAsync"/>. Keep the returned export (and dispose it to
    /// stop exporting).
    /// </summary>
    public static ExportedNode<T> Export<T>(this MemoFactory factory, IStampedGetR<T> node, Func<StaleNotification, Task> publishStale)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is not MemoHandlR<T> handle)
        {
            throw new ArgumentException("Only MemoizR value nodes (signals, memos, concurrent nodes) can be exported.", nameof(node));
        }
        return ExportCore(factory, handle, publishStale, (builder, notify) => builder.CreateReaction(node, _ => notify()));
    }

    /// <summary>
    /// Export a plain signal. (A separate overload because <see cref="Signal{T}"/> surfaces its
    /// stamped interface as <c>IStampedGetR&lt;T?&gt;</c>; this keeps the export typed to the
    /// signal's <typeparamref name="T"/>.)
    /// </summary>
    public static ExportedNode<T> Export<T>(this MemoFactory factory, Signal<T> signal, Func<StaleNotification, Task> publishStale)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return ExportCore(factory, signal, publishStale, (builder, notify) => builder.CreateReaction(signal, _ => notify()));
    }

    private static ExportedNode<T> ExportCore<T>(
        MemoFactory factory,
        MemoHandlR<T> handle,
        Func<StaleNotification, Task> publishStale,
        Func<ReactionBuilder, Action, Reaction> wireReaction)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(publishStale);
        if (!ReferenceEquals(handle.Context, factory.Context))
        {
            throw new ArgumentException("The exported node must belong to this factory's context.", nameof(handle));
        }
        if (!factory.Context.StampsEnabled)
        {
            // The one hard boundary of MemoFactoryOptions.DisableCausalityStamps: an export
            // from a stamps-disabled context would advertise no ordering evidence at all.
            throw new InvalidOperationException(
                "This context was created with MemoFactoryOptions.DisableCausalityStamps; its nodes cannot be exported to peers.");
        }
        if (MirrorLocals.TouchesMirrorLocal(handle))
        {
            // Re-exporting a mirror -- or any node derived from one -- would advertise LOCAL
            // adoption stamps as evidence; the origin's evidence lives beside the graph in
            // RemoteSignal.Publication, so a downstream barrier would render torn origin
            // snapshots as consistent. This is the fail-fast half: a lazy memo has no wired
            // sources until its first evaluation, so ExportedNode.PullAsync re-checks at the
            // wire egress.
            throw new InvalidOperationException(MirrorLocals.ReExportMessage);
        }

        var exported = new ExportedNode<T>(handle, publishStale);
        var reaction = wireReaction(factory.BuildReaction($"export {handle.Label}"), exported.NotifyStale);
        exported.AttachReaction(reaction);
        return exported;
    }

    /// <summary>
    /// Create the consumer-side mirror of a remote exported node: a local eager signal fed by
    /// the adoption protocol (see <see cref="RemoteSignal{T}"/>), pulling the host's truth via
    /// <paramref name="pull"/> (your transport's request path). Feed transport deliveries to
    /// <see cref="RemoteSignal{T}.OnStaleAsync"/> / <see cref="RemoteSignal{T}.OnValueAsync"/>.
    /// Pass <paramref name="nodeId"/> (the exported node's id) on multiplexed bridges so a
    /// misrouted payload is rejected up front; without it, the first delivered payload pins
    /// the binding.
    /// </summary>
    public static RemoteSignal<T> CreateRemoteSignal<T>(
        this MemoFactory factory,
        string label,
        T initialValue,
        Func<Task<ValuePayload<T>>> pull,
        Func<Task>? onPeerReset = null,
        int? nodeId = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(pull);
        var local = factory.CreateEagerRelativeSignal(label, initialValue);
        MirrorLocals.Register(local); // re-exporting a mirror is refused (see ExportCore)
        return new RemoteSignal<T>(local, pull, nodeId)
        {
            OnPeerReset = onPeerReset,
        };
    }
}
