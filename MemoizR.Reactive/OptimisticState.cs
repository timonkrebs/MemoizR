using System.Collections.Immutable;

namespace MemoizR.Reactive;

// A value source that DELEGATES to a real graph node (OptimisticState wraps its composed view
// memo). ReactionBuilder unwraps this so the multi-parameter CreateReaction overloads register
// the dependency and record its stamps against the backing node -- a plain IStateGetR wrapper
// would be skipped by RegisterDependencies and read untracked on the isolated scope, so the
// reaction would never re-run when the wrapped state changes.
internal interface INodeBackedGetR
{
    SignalHandlR Node { get; }
}

/// <summary>
/// An optimistic view over a source of truth (ADR 0007): reads return the source value with
/// every in-flight optimistic patch applied on top, so a user action can instantly project its
/// expected future state while the real process (network write, revalidation) runs in the
/// background. Rollback is STRUCTURAL -- a failed action simply removes its patch, the source
/// was never touched -- so overlapping actions compose: each owns its patch, and one action's
/// rollback can never clobber another's projection. The overlay and the composed view are
/// ordinary graph nodes; propagation, memoization, laziness and causality evidence all apply
/// unchanged. Patch functions run inside the view's computation and must be pure.
/// </summary>
public sealed class OptimisticState<T> : IStateGetR<T>, INodeBackedGetR
{
    SignalHandlR INodeBackedGetR.Node => view;

    // The overlay is an EagerRelativeSignal so patch add/remove is an atomic read-modify-write
    // under the signal's own lock. Constructed through the internal ctor on purpose: the strict
    // Sendable check would reject the delegate-carrying tuples, but the list is immutable and
    // runtime-owned -- the user-facing value type T is still checked by CreateMemoizR below.
    private readonly EagerRelativeSignal<ImmutableList<(long Id, Func<T, T> Patch)>> overlay;
    private readonly MemoizR<T> view;
    private long nextPatchId;

    internal OptimisticState(MemoFactory factory, IStateGetR<T> source, string label)
    {
        overlay = new(ImmutableList<(long Id, Func<T, T> Patch)>.Empty, factory.Context)
        {
            Label = $"{label}.Overlay"
        };
        view = factory.CreateMemoizR($"{label}.View", async () =>
        {
            var baseValue = await source.Get();
            var patches = await overlay.Get();
            return patches.Aggregate(baseValue, (acc, p) => p.Patch(acc));
        });
    }

    /// <summary>The composed read: source of truth with the in-flight patches applied.</summary>
    public Task<T> Get() => view.Get();

    // Applies a patch, admitting it through the caller's callback INSIDE the overlay's
    // read-modify-write: admission (recording the id with the owning run) and landing are one
    // atomic step against the run's close sweep, whose removals serialize on this same overlay
    // lock -- so a patch can never land unrecorded and outlive the run's settlement. Only
    // OptimisticActionContext calls this; a rejected patch leaves the overlay untouched.
    internal async Task<bool> TryApplyPatchAsync(Func<T, T> patch, Func<long, bool> admit)
    {
        var id = Interlocked.Increment(ref nextPatchId);
        var admitted = false;
        await overlay.Set(list =>
        {
            admitted = admit(id);
            return admitted ? list.Add((id, patch)) : list;
        }).ConfigureAwait(false);
        return admitted;
    }

    // A run's patches on this state are removed in ONE read-modify-write: dropping them
    // individually would expose intermediate frames in which a later patch applies without the
    // earlier patch it builds on (a patch assuming the shape its predecessor produced could
    // then throw, or project an impossible value to a concurrent Get).
    internal Task RemovePatchesAsync(IReadOnlyCollection<long> ids)
    {
        return overlay.Set(list => list.RemoveAll(p => ids.Contains(p.Id)));
    }
}
