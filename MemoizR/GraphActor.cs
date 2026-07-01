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
public sealed class GraphActor : IExecutor
{
    private readonly Channel<Action> turns = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });

    // Exact executor identity: set only for the duration of a turn, and turns are synchronous,
    // so the marker can be thread-affine.
    [ThreadStatic]
    private static GraphActor? running;

    public GraphActor()
    {
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

    private async Task RunLoop()
    {
        // Turn delegates own their exceptions (they complete a TCS), so nothing here can throw
        // past the marker bookkeeping -- the loop survives any turn.
        await foreach (var turn in turns.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            running = this;
            try
            {
                turn();
            }
            finally
            {
                running = null;
            }
        }
    }
}
