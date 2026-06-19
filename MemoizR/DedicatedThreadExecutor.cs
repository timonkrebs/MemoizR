using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace MemoizR;

/// <summary>
/// An <see cref="IExecutor"/> that owns one background thread and runs every enqueued work item
/// on it, FIFO -- the closest .NET gets to a Swift actor's serial executor (SE-0392). The thread
/// installs its own <see cref="SynchronizationContext"/>, so async continuations of enqueued
/// work return to the thread too: state touched only from this executor is single-threaded by
/// construction, across awaits.
/// </summary>
/// <remarks>
/// Disposal completes the queue and (when not called from the executor thread itself) waits for
/// the remaining items to drain. Work enqueued -- or continuations posted -- after shutdown falls
/// back to the thread pool so nothing is lost, but the single-thread isolation guarantee ends at
/// dispose. Exceptions escaping a raw posted callback (e.g. a user's <c>async void</c> rethrown
/// onto the captured context) are re-thrown on the thread pool, preserving the platform's
/// "async-void exceptions are fatal" convention without wedging the executor's queue.
/// </remarks>
public sealed class DedicatedThreadExecutor : IExecutor, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly Thread thread;
    private int disposed;

    public DedicatedThreadExecutor(string? name = null)
    {
        thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = name ?? nameof(DedicatedThreadExecutor),
        };
        thread.Start();
    }

    public bool IsCurrent => Thread.CurrentThread == thread;

    public void Enqueue(Action work)
    {
        Post(static state => ((Action)state!)(), work);
    }

    private void Post(SendOrPostCallback callback, object? state)
    {
        try
        {
            queue.Add((callback, state));
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Shut down (or racing shutdown): execute on the thread pool rather than dropping
            // the work -- a lost async continuation would leave its awaiter pending forever.
            ThreadPool.QueueUserWorkItem(s => callback(s), state, preferLocal: false);
        }
    }

    private void RunLoop()
    {
        SynchronizationContext.SetSynchronizationContext(new ExecutorSynchronizationContext(this));
        foreach (var (callback, state) in queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch (Exception e)
            {
                // Keep the loop (and every queued item behind this one) alive; surface the
                // exception with platform semantics instead of swallowing it.
                var captured = ExceptionDispatchInfo.Capture(e);
                ThreadPool.QueueUserWorkItem(static c => ((ExceptionDispatchInfo)c!).Throw(), captured, preferLocal: false);
            }
        }

        // The loop owns the queue's lifetime: dispose only after consumption ended, so Dispose()
        // racing an Enqueue never pulls the collection out from under the consumer. A concurrent
        // Add hitting the disposed collection lands in Post's thread-pool fallback.
        queue.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        queue.CompleteAdding();

        // Drain: remaining items still run. Joining from the executor's own thread would
        // deadlock (the loop frame is beneath us); the loop exits on its own after this item.
        if (!IsCurrent)
        {
            thread.Join();
        }
    }

    // Routes async continuations (and explicit Posts on the captured context) back into the
    // executor's queue, which is what extends the single-thread guarantee across awaits.
    private sealed class ExecutorSynchronizationContext(DedicatedThreadExecutor owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            owner.Post(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (owner.IsCurrent)
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim();
            ExceptionDispatchInfo? error = null;
            owner.Post(s =>
            {
                try
                {
                    d(s);
                }
                catch (Exception e)
                {
                    error = ExceptionDispatchInfo.Capture(e);
                }
                finally
                {
                    done.Set();
                }
            }, state);
            done.Wait();
            error?.Throw();
        }

        public override SynchronizationContext CreateCopy()
        {
            return this;
        }
    }
}
