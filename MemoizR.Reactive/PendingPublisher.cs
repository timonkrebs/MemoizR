namespace MemoizR.Reactive;

// The one pending-flag publisher (ADR 0007), shared by ReactionBase.IsPending,
// Transition.Pending and ReactiveAction.IsPending: a lazily created boolean signal that a
// detached pump converges onto a caller-owned snapshot. The callers flip their state inside
// invalidation cascades, committing evaluations, or update finallys -- all places where a Set
// would re-enter the graph -- so every publish runs on a detached, transition-suppressed flow.
// Publishes are chained in schedule order and each link reads the CURRENT snapshot at Set time:
// every state change schedules a link, so the last link always publishes the latest truth no
// matter how the flips raced; intermediate links can lag, which is why the signal is documented
// as convergent rather than edge-accurate.
internal sealed class PendingPublisher(Context context, Func<bool> snapshot, string label)
{
    private readonly Lock gate = new();
    private Signal<bool>? signal;
    private Task chain = Task.CompletedTask;

    // The reactive projection; created on first access so nodes that are never observed
    // reactively pay nothing. The signal's own equality short-circuit keeps redundant
    // convergence publishes from propagating.
    public IStateGetR<bool> Signal
    {
        get
        {
            if (signal is null)
            {
                LazyInitializer.EnsureInitialized(ref signal,
                    () => new Signal<bool>(snapshot(), context) { Label = label });
                // Fold any state change that raced the lazy creation into the signal.
                Publish();
            }
            return signal!;
        }
    }

    public void Publish()
    {
        if (signal is null)
        {
            return;
        }

        lock (gate)
        {
            var prev = chain;
            chain = Task.Run(async () =>
            {
                // The pump inherits the scheduling flow's ExecutionContext -- including any
                // ambient transition tag, which must not re-register the signal's observers
                // on the transition (it could never settle).
                TransitionFlow.Suppress();
                await prev.ConfigureAwait(false);
                try
                {
                    await signal!.Set(snapshot()).ConfigureAwait(false);
                }
                catch
                {
                    // A failed publish must not break the chain; the next state change
                    // schedules another link that converges the signal.
                }
            });
        }
    }
}
