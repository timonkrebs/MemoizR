using System.Threading.Channels;

namespace MemoizR;

/// <summary>
/// The serial executor at the heart of the experimental actor engine (issue #36 layer 5, ADR
/// 0006): one per <see cref="Context"/>, owning ALL graph bookkeeping -- cache-state
/// transitions, generations, dependency capture, link rewiring, invalidation cascades -- as
/// <b>synchronous turns</b> processed one at a time off a channel. User computations never run
/// in turns; an evaluation is a Begin-turn, off-actor compute, Commit-turn transaction. Because
/// a turn can never await and the actor is never "held" across user code, the lock-ordering
/// concerns of the lock-based engine (concurrency.md §9) cannot exist here by construction.
/// </summary>
/// <remarks>
/// Implements <see cref="IExecutor"/> (layer 4): the graph actor IS a custom executor, with
/// exact <see cref="IsCurrent"/> identity while a turn runs, so the actor-confined state can be
/// guarded by the same <see cref="ExecutorExtensions.AssertIsolated"/> dynamic checks as any
/// other executor-isolated state. The loop parks on its own channel when idle and holds no
/// external roots, so an unreachable actor (its Context dropped) is collectable, loop and all.
/// </remarks>
[Sendable] // internally synchronized by design: safe to share across flows (and to hold in statics, see MZR004)
public sealed class GraphActor : IExecutor
{
    private readonly Channel<Action> turns = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });

    // Exact executor identity: set only for the duration of a turn, and turns are synchronous,
    // so the marker can be thread-affine.
    [ThreadStatic]
    private static GraphActor? running;

    // Installed for the duration of every turn: ASYNC work enqueued on the actor (a reaction
    // pinned to it as its IExecutor) captures this context at its awaits, so each continuation
    // segment posts back as a new turn instead of escaping to the thread pool -- IsCurrent
    // stays true across the whole async body, exactly like DedicatedThreadExecutor's installed
    // context. Graph bookkeeping turns never await, so for them the context is inert.
    private readonly SynchronizationContext continuationsContext;

    public GraphActor()
    {
        continuationsContext = new ActorSynchronizationContext(this);
        _ = RunLoop();
    }

    public bool IsCurrent => ReferenceEquals(running, this);

    /// <summary>
    /// Runs a synchronous bookkeeping turn on the actor and returns its result. Turns are the
    /// ONLY code allowed to touch actor-confined node state, and they must never block or
    /// await -- the whole design rests on the actor never being held across user code. A Run
    /// from within a turn executes inline (it is part of the current turn's atomic step;
    /// queueing it would deadlock the turn against itself).
    /// </summary>
    public Task<T> Run<T>(Func<T> turn)
    {
        if (IsCurrent)
        {
            try
            {
                return Task.FromResult(turn());
            }
            catch (Exception e)
            {
                return Task.FromException<T>(e);
            }
        }

        // RunContinuationsAsynchronously: completing the TCS inside the loop must not run the
        // awaiting flow's continuation inline on the loop thread, where it would stall every
        // queued turn behind it.
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        turns.Writer.TryWrite(() =>
        {
            try
            {
                tcs.SetResult(turn());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    public Task Run(Action turn)
    {
        return Run<object?>(() =>
        {
            turn();
            return null;
        });
    }

    // IExecutor: lets the actor serve as a seat for layer-4 consumers too (e.g. a reaction
    // pinned to the graph actor). The delegate carries its own completion/exception handling
    // per the IExecutor contract, so the dropped task here loses nothing.
    public void Enqueue(Action work)
    {
        _ = Run(work);
    }

    // A turn that is queued UNCONDITIONALLY (no inline-if-current shortcut): the posted
    // continuations of async executor work must interleave with other turns, not run inside
    // the turn that posted them. Exceptions are contained (the loop must survive any turn);
    // posted work owns its own error handling, exactly like Enqueue's dropped task.
    private void QueueTurn(Action work)
    {
        turns.Writer.TryWrite(() =>
        {
            try
            {
                work();
            }
            catch
            {
                // Deliberately swallowed: see the Enqueue contract.
            }
        });
    }

    private async Task RunLoop()
    {
        // Turn delegates own their exceptions (they complete a TCS), so nothing here can throw
        // past the marker bookkeeping -- the loop survives any turn. The loop hops pool threads
        // between turns, so the continuation context is installed and restored PER TURN.
        await foreach (var turn in turns.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            running = this;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(continuationsContext);
            try
            {
                turn();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                running = null;
            }
        }
    }

    // Posts continuations back to the actor as fresh turns. Each continuation SEGMENT (the code
    // between awaits) is synchronous, so the turns-never-await invariant holds; the actor is
    // never blocked across the await itself -- other turns interleave freely.
    private sealed class ActorSynchronizationContext(GraphActor actor) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // ALWAYS a fresh queued turn -- never Run, whose inline-if-current fast path would
            // execute the continuation inside the posting turn: a Task.Yield() from actor work
            // would then be a no-op instead of an interleaving point, and the "continuation"
            // would run reentrantly within the very turn that posted it.
            actor.QueueTurn(() => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (actor.IsCurrent)
            {
                d(state);
                return;
            }

            actor.Run(() => d(state)).GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy()
        {
            return this;
        }
    }
}
