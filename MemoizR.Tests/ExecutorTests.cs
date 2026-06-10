namespace MemoizR.Tests;

// Contracts of the executor abstraction (issue #36, the SE-0392 "custom actor executors"
// analog): DedicatedThreadExecutor is a true serial isolation seat (FIFO, one thread, async
// continuations return to it, disposal drains, post-shutdown work falls back to the pool
// instead of being lost), SynchronizationContextExecutor adapts a context faithfully,
// AssertIsolated is the executor-flavored preconditionIsolated, and reactions actually run
// their Execute on the configured executor -- factory-level or per-builder.
public class ExecutorTests
{
    [Fact(Timeout = 10000)]
    public async Task DedicatedThreadExecutor_RunsWorkFifoOnOneForeignThread()
    {
        using var executor = new DedicatedThreadExecutor("test-executor");
        var order = new List<int>();
        var threads = new HashSet<int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < 100; i++)
        {
            var n = i;
            executor.Enqueue(() =>
            {
                order.Add(n);
                threads.Add(Environment.CurrentManagedThreadId);
                if (n == 99)
                {
                    done.SetResult();
                }
            });
        }

        await done.Task;
        Assert.Equal(Enumerable.Range(0, 100), order);
        Assert.Single(threads);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
    }

    [Fact(Timeout = 10000)]
    public async Task DedicatedThreadExecutor_AsyncContinuations_StayOnItsThread()
    {
        using var executor = new DedicatedThreadExecutor();
        var tcs = new TaskCompletionSource<(int Before, int After, bool IsCurrent)>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The installed SynchronizationContext must route the await's continuation back onto
        // the executor thread -- that is what makes it a single-threaded isolation seat.
        executor.Enqueue(async void () =>
        {
            var before = Environment.CurrentManagedThreadId;
            await Task.Delay(20);
            tcs.SetResult((before, Environment.CurrentManagedThreadId, executor.IsCurrent));
        });

        var (before, after, isCurrent) = await tcs.Task;
        Assert.Equal(before, after);
        Assert.True(isCurrent);
    }

    [Fact(Timeout = 10000)]
    public async Task DedicatedThreadExecutor_Dispose_DrainsAlreadyEnqueuedWork()
    {
        var executed = 0;
        await Task.Run(() =>
        {
            var executor = new DedicatedThreadExecutor();
            for (var i = 0; i < 20; i++)
            {
                executor.Enqueue(() =>
                {
                    Thread.Sleep(1);
                    Interlocked.Increment(ref executed);
                });
            }

            executor.Dispose(); // blocks until the queue drained
        });

        Assert.Equal(20, Volatile.Read(ref executed));
    }

    [Fact(Timeout = 10000)]
    public async Task DedicatedThreadExecutor_EnqueueAfterDispose_RunsOnThreadPool_NotIsolated()
    {
        var executor = new DedicatedThreadExecutor();
        await Task.Run(executor.Dispose);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.Enqueue(() => tcs.SetResult(executor.IsCurrent));

        // The work must not be lost -- but it also must not claim executor isolation.
        Assert.False(await tcs.Task);
    }

    [Fact(Timeout = 10000)]
    public async Task AssertIsolated_PassesOnTheExecutor_ThrowsOffIt()
    {
        using var executor = new DedicatedThreadExecutor();
        Assert.Throws<InvalidOperationException>(() => executor.AssertIsolated());

        var tcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.Enqueue(() =>
        {
            try
            {
                executor.AssertIsolated();
                tcs.SetResult(null);
            }
            catch (Exception e)
            {
                tcs.SetResult(e);
            }
        });

        Assert.Null(await tcs.Task);
    }

    [Fact(Timeout = 10000)]
    public async Task SynchronizationContextExecutor_PostsToTheContext_AndIsCurrentTracksIt()
    {
        var context = new SelfInstallingContext();
        var executor = new SynchronizationContextExecutor(context);
        Assert.False(executor.IsCurrent);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.Enqueue(() => tcs.SetResult(executor.IsCurrent));

        Assert.True(await tcs.Task);
        Assert.Equal(1, context.Posted);
    }

    [Fact(Timeout = 10000)]
    public async Task Reaction_WithFactoryExecutor_RunsExecuteOnIt_AndConverges()
    {
        using var executor = new DedicatedThreadExecutor();
        var f = new MemoFactory().AddExecutor(executor);
        var v1 = f.CreateSignal(1);

        var last = 0;
        var onExecutor = true;
        var r = f.BuildReaction().CreateReaction(v1, v =>
        {
            onExecutor &= executor.IsCurrent;
            last = v;
        });

        await v1.Set(2);
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref last) == 2);

        Assert.Equal(2, last);
        Assert.True(onExecutor, "Execute ran off the configured executor");
    }

    [Fact(Timeout = 10000)]
    public async Task ReactionBuilder_AddExecutor_OverridesTheFactoryLevelExecutor()
    {
        using var perReaction = new DedicatedThreadExecutor();
        var f = new MemoFactory(); // no factory-level executor
        var v1 = f.CreateSignal(1);

        var sawExecutor = false;
        var r = f.BuildReaction()
            .AddExecutor(perReaction)
            .CreateReaction(v1, _ => sawExecutor = perReaction.IsCurrent);

        await v1.Set(2);
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref sawExecutor));

        Assert.True(sawExecutor);
    }

    // Runs posted callbacks on the thread pool but installs itself as Current for their
    // duration, like the UI contexts do -- the shape IsCurrent is designed against.
    private sealed class SelfInstallingContext : SynchronizationContext
    {
        public int Posted;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posted);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            });
        }
    }
}
