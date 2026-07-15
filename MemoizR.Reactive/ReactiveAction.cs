namespace MemoizR.Reactive;

/// <summary>
/// The process-layer write primitive (ADR 0007), MemoizR's analog of Solid 2.0's
/// generator-driven actions: a reusable async body that projects optimistic patches, runs the
/// real process (network write, revalidation), and is automatically rolled back -- patches
/// dropped, source untouched -- when it faults or is cancelled. Each <see cref="Run"/> executes
/// on a detached flow tagged with its own <see cref="Transition"/>, so the projection, the
/// confirmed write and the rollback all have their effect wavefronts tracked; the run's
/// <see cref="ActionRun.Settled"/> completes when the UI reflects the final outcome.
/// </summary>
public sealed class ReactiveAction<TPayload>
{
    private readonly Context context;
    private readonly Func<TPayload, OptimisticActionContext, Task> body;
    private int runningCount;
    private readonly PendingPublisher pendingPublisher;

    internal ReactiveAction(Context context, Func<TPayload, OptimisticActionContext, Task> body, string label)
    {
        this.context = context;
        this.body = body;
        pendingPublisher = new(context, () => IsPendingSnapshot, $"{label}.IsPending");
    }

    /// <summary>Snapshot: is any run of this action still executing?</summary>
    public bool IsPendingSnapshot => Volatile.Read(ref runningCount) > 0;

    /// <summary>
    /// The reactive "a run is in flight" flag -- an ordinary graph node (disable the submit
    /// button with a reaction on it). Converges on <see cref="IsPendingSnapshot"/>.
    /// </summary>
    public IStateGetR<bool> IsPending => pendingPublisher.Signal;

    public ActionRun Run(TPayload payload)
    {
        // The transition is created untagged and attached to the BODY flow below: the caller's
        // flow never carries this run's writes.
        var transition = new Transition(context, tagAmbientFlow: false);
        var cts = new CancellationTokenSource();
        var ctx = new OptimisticActionContext(cts.Token);
        if (Interlocked.Increment(ref runningCount) == 1)
        {
            pendingPublisher.Publish();
        }

        var completion = Task.Run(async () =>
        {
            // Tag the detached body flow: the optimistic projection, the confirmed base write,
            // and the rollback all register their effect wavefronts on this run's transition.
            // Local to this async flow; the caller's ambient tag is untouched.
            TransitionFlow.Current = transition;
            // The detached body also inherits the CALLER's lock-scope key: a Run issued from
            // inside a graph evaluation would make the body's Sets recursive acquisitions of
            // the caller's held ContextLock. A forced fresh scope gives the body its own.
            var scope = context.ForceNewScope();
            try
            {
                await body(payload, ctx).ConfigureAwait(false);
            }
            finally
            {
                // Success and failure alike drop this run's patches: on success the confirmed
                // source value carries the truth (the debounce coalesces the hand-off for
                // reactions), on failure the removal IS the rollback -- structural, so it can
                // never clobber a concurrent action's projection. Sealing the transition last
                // makes Settled mean "the UI reflects the final outcome, patches gone".
                await ctx.DropPatchesAsync().ConfigureAwait(false);
                transition.Dispose();
                context.CleanScope();
                GC.KeepAlive(scope);
                if (Interlocked.Decrement(ref runningCount) == 0)
                {
                    pendingPublisher.Publish();
                }
            }
        });

        return new ActionRun(completion, transition, cts);
    }
}

/// <summary>
/// One execution of a <see cref="ReactiveAction{TPayload}"/>. <see cref="Completion"/> carries
/// the body's outcome (fault, cancellation); <see cref="Settled"/> tracks the effect wavefront
/// -- both ends of Solid's split between the process and the propagation it causes.
/// </summary>
public sealed class ActionRun
{
    private readonly CancellationTokenSource cts;

    internal ActionRun(Task completion, Transition transition, CancellationTokenSource cts)
    {
        Completion = completion;
        Transition = transition;
        this.cts = cts;
    }

    /// <summary>The body's outcome: faults and cancellations surface here, after rollback.</summary>
    public Task Completion { get; }

    /// <summary>The run's write wavefront; pending state and settlement live here.</summary>
    public Transition Transition { get; }

    /// <summary>Shorthand for <c>Transition.Settled</c>.</summary>
    public Task Settled => Transition.Settled;

    /// <summary>Cancels the run's <see cref="OptimisticActionContext.Token"/>; the body's
    /// cancellation then triggers the same automatic rollback as a fault.</summary>
    public void Cancel() => cts.Cancel();
}

/// <summary>
/// Handed to an action body: the run's cancellation token plus the optimistic projection API.
/// Every patch applied through it is owned by this run and dropped when the run ends,
/// whatever the outcome.
/// </summary>
public sealed class OptimisticActionContext
{
    private readonly Lock gate = new();
    // One entry per touched optimistic state: the ids this run applied to it plus the
    // type-erased batch remover. Grouped so the rollback drops all of a run's patches on one
    // state in a single read-modify-write -- removing them one by one would expose frames in
    // which a later patch applies without the earlier patch it builds on.
    private readonly Dictionary<object, (Func<IReadOnlyCollection<long>, Task> RemoveBatch, List<long> Ids)> applied = new();
    private bool closed;

    internal OptimisticActionContext(CancellationToken token)
    {
        Token = token;
    }

    public CancellationToken Token { get; }

    /// <summary>
    /// Instantly projects the expected future state onto the optimistic view; the patch stays
    /// applied until this run ends (confirmed or rolled back). Only valid while the run's body
    /// executes: a context leaked to fire-and-forget work throws here after the run ended --
    /// its patch would be owned by no drop and stay applied forever.
    /// </summary>
    public async Task Apply<T>(OptimisticState<T> state, Func<T, T> patch)
    {
        lock (gate)
        {
            ThrowIfClosed();
        }

        var id = await state.ApplyPatchAsync(patch).ConfigureAwait(false);
        var closedMidApply = false;
        lock (gate)
        {
            if (closed)
            {
                // The run's drop swept between the overlay write above and this recording: the
                // patch is orphaned unless removed here, on this flow.
                closedMidApply = true;
            }
            else
            {
                if (!applied.TryGetValue(state, out var entry))
                {
                    entry = (state.RemovePatchesAsync, new List<long>());
                    applied[state] = entry;
                }
                entry.Ids.Add(id);
            }
        }

        if (closedMidApply)
        {
            await state.RemovePatchesAsync([id]).ConfigureAwait(false);
            ThrowIfClosed();
        }
    }

    private void ThrowIfClosed()
    {
        if (closed)
        {
            throw new InvalidOperationException(
                "This action run has completed; optimistic patches can only be applied while the run's body executes.");
        }
    }

    internal async Task DropPatchesAsync()
    {
        (Func<IReadOnlyCollection<long>, Task> RemoveBatch, List<long> Ids)[] batches;
        lock (gate)
        {
            closed = true;
            batches = [.. applied.Values];
            applied.Clear();
        }

        foreach (var (removeBatch, ids) in batches)
        {
            try
            {
                await removeBatch(ids).ConfigureAwait(false);
            }
            catch
            {
                // A failed removal must not mask the body's outcome; the overlay is runtime
                // state and the next successful write converges it.
            }
        }
    }
}
