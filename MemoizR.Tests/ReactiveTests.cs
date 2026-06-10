using xRetry;

namespace MemoizR.Tests;

public class ReactiveTests
{
    [Fact(Timeout = 1000)]
    public async Task TestReactive()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());
        Assert.Equal(1, await v1.Get());

        var m1 = f.BuildReaction()
        .CreateReaction(v1, v => { });

        await v1.Set(2);
    }

    [RetryFact(3, 200)]
    public async Task TestAsyncEnumerable()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var invocationCount = 0;
        var result = 0;

        var m1 = f.BuildReaction()
        .CreateAsyncEnumerableExperimental(v1);

        await Task.Delay(100);

        var t = new Task(async () =>
        {
            await foreach (var m in m1)
            {
                result = m;
                invocationCount++;
            }
        });

        t.Start();

        await Task.Delay(10);
        Assert.Equal(0, result);
        Assert.Equal(0, invocationCount);

        await v1.Set(2);
        await Task.Delay(20);
        Assert.Equal(2, result);
        Assert.Equal(1, invocationCount);

        await v1.Set(3);
        await Task.Delay(20);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);

        await v1.Set(3);
        await Task.Delay(20);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);

        await v1.Set(4);
        await Task.Delay(20);
        Assert.Equal(4, result);
        Assert.Equal(3, invocationCount);
        Assert.NotNull(m1);
    }

    [Fact(Timeout = 2000)]
    public async Task TestAsyncEnumerableWithBackpressure()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var invocationCount = 0;
        var result = 0;

        var m1 = f.BuildReaction()
        .CreateAsyncEnumerableExperimental(v1);

        await Task.Delay(100);

        var _ = Task.Run(async () =>
        {
            await foreach (var m in m1)
            {
                result = m;
                invocationCount++;
                await Task.Delay(100);
            }
        });

        await Task.Delay(200);
        Assert.Equal(0, result);
        Assert.Equal(0, invocationCount);

        await v1.Set(2);
        Assert.Equal(0, result);
        Assert.Equal(0, invocationCount);
        await Task.Delay(200);
        Assert.Equal(2, result);
        Assert.Equal(1, invocationCount);

        await v1.Set(3);
        Assert.Equal(2, result);
        Assert.Equal(1, invocationCount);
        await Task.Delay(200);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);

        await v1.Set(3);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);
        await Task.Delay(200);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);

        await v1.Set(4);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);
        await Task.Delay(200);
        Assert.Equal(4, result);
        Assert.Equal(3, invocationCount);
        Assert.NotNull(m1);
    }

    [Fact(Timeout = 1000)]
    public async Task TestReactiveInvocations()
    {
        var invocations = 0;
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());

        var m1 = f.BuildReaction()
        .CreateReaction(v1, v => invocations++);

        await Task.Delay(30);
        Assert.Equal(1, invocations);
        await Task.Delay(100);

        await v1.Set(2);
        await Task.Delay(100);

        Assert.Equal(2, invocations);

        await v1.Set(2);
        await Task.Delay(20);

        Assert.Equal(2, invocations);
    }

    // Depends on the GC collecting the reaction; collection timing is nondeterministic, so retry.
    [RetryFact(3, 200)]
    public async Task TestAutoSubscriptionHandling()
    {
        var invocations = new Invocations { Count = 0 };
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);

        Assert.Equal(1, await v1.Get());

        CreateReaction(f, v1, invocations);

        GC.Collect();
        GC.Collect();
        await Task.Delay(100);
        GC.Collect();
        GC.Collect();

        await Task.Delay(30);
        Assert.Equal(1, invocations.Count);
        await Task.Delay(100);

        await v1.Set(2);
        await Task.Delay(100);

        Assert.Equal(1, invocations.Count);

        await v1.Set(2);
        await Task.Delay(20);

        Assert.Equal(1, invocations.Count);
    }

    private void CreateReaction(MemoFactory f, Signal<int> v1, Invocations invocations)
    {
        f.BuildReaction()
        .CreateReaction(v1, v => invocations.Count++);
    }

    private class Invocations
    {
        public int Count { get; set; }
    }

    // Timing-sensitive: asserts the debounced reaction's observed value after fixed delays.
    // Retry like the sibling TestThreadSafety3-6 instead of relying on a knife-edge timeout.
    [RetryFact(3, 200)]
    public async Task TestThreadSafety()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var invocationCount = 0;
        var result = 0;
        var _ = Task.Run(() =>
            {
                var r1 = f.BuildReaction()
                        .CreateReaction(m1, m =>
                        {
                            invocationCount++;
                            result = m;
                        });
            });

        await Task.Delay(200);

        _ = v1.Set(1000);
        for (var i = 0; i < 100; i++)
        {
            _ = v1.Set(i);
        }

        for (var i = 0; i < 21; i++)
        {
            var j = i;
            _ = v1.Set(j);
            _ = v1.Set(j);
        }

        await Task.Delay(200);

        Assert.Equal(40, await m1.Get());
        Assert.Equal(40, result);
        Assert.Equal(await m1.Get(), result);

        await Task.Delay(100);

        Assert.True(invocationCount >= 1, "Must be invoked at least once");
    }

    [RetryFact(3, 200)]
    public async Task TestThreadSafety2()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var invocationCount = 0;
        var r1 = f.BuildReaction()
        .AddDebounceTime(TimeSpan.FromMilliseconds(10))
        .CreateReaction(m1, m => invocationCount++);
        await Task.Delay(50);
        var tasks = new List<Task>();
        for (var i = 0; i < 200; i++)
        {
            var j = i;
            tasks.Add(Task.Run(async () => await v1.Set(j)));
            tasks.Add(Task.Run(async () => await v1.Set(j)));
        }
        var resultM1 = 0;
        tasks.Add(Task.Run(async () => resultM1 = await m1.Get()));

        await Task.WhenAll(tasks);
        await Task.Run(async () => await v1.Set(200));

        await Task.Delay(50);

        Assert.Equal(400, await m1.Get());
        Assert.NotEqual(400, resultM1);
        Assert.True(invocationCount > 1, "Must be invoked more than once");
    }

    [RetryFact(3, 5000)]
    [Trait("Category", "Unit")]
    public async Task TestThreadSafety3()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);

        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var result1 = 0;
        var r1 = f.BuildReaction().CreateReaction(m1, m => result1 = m);
        var result2 = 0;
        var r2 = f.BuildReaction().CreateReaction(m1, m => result2 = m);

        await Task.Delay(100);

        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            var j = i;
            tasks.Add(Task.Run(async () => await v1.Set(j)));
            await Task.Delay(35);
        }
        await Task.WhenAll(tasks);
        tasks.Add(Task.Run(async () => await v1.Set(41)));

        await Task.Delay(1);
        var resultM1 = 0;
        tasks.Add(Task.Run(async () => resultM1 = await m1.Get()));

        await Task.WhenAll(tasks);
        await Task.Delay(100);

        Assert.Equal(82, await m1.Get());

        Assert.Equal(82, resultM1);
        Assert.Equal(82, result1);
        Assert.Equal(82, result2);
    }

    [RetryFact(3, 200)]
    public async Task TestThreadSafety4()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);

        var memoInvocationCount = 0;
        var m1 = f.CreateMemoizR("m1", async () =>
        {
            memoInvocationCount++;
            return await v1.Get() * 2;
        });

        var m2 = f.CreateMemoizR("m2", async () =>
        {
            return await m1.Get() * 2;
        });

        var invocationCountR1 = 0;
        var r1 = f.BuildReaction().CreateReaction(m2, m => invocationCountR1++);
        var invocationCountR2 = 0;
        var r2 = f.BuildReaction().CreateReaction(m2, m => invocationCountR2++);

        await Task.Delay(100);

        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            var j = i;
            tasks.Add(Task.Run(async () => await v1.Set(j)));
            await Task.Delay(35);
        }

        tasks.Add(Task.Run(async () => await v1.Set(41)));

        await Task.WhenAll(tasks);

        Assert.Equal(82, await m1.Get());

        await Task.Delay(100);

        Assert.Equal(42, memoInvocationCount);
        Assert.Equal(42, invocationCountR1);
        Assert.Equal(42, invocationCountR2);
    }

    [RetryFact(3, 5000)]
    public async Task TestThreadSafety5()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);

        var memoInvocationCount = 0;
        var m1 = f.CreateMemoizR("m1", async () =>
        {
            memoInvocationCount++;
            return await v1.Get() * 2;
        });

        var m2 = f.CreateMemoizR("m2", async () =>
        {
            return await m1.Get() * 2;
        });

        var invocationCountR1 = 0;
        var r1 = f.BuildReaction().CreateReaction(m2, m => invocationCountR1++);
        var invocationCountR2 = 0;
        var r2 = f.BuildReaction().CreateReaction(m2, m => invocationCountR2++);

        await Task.Delay(100);

        var tasks = Enumerable.Range(1, 100_000).Select(i => new Task(async () => await v1.Set(i))).ToList();
        Parallel.ForEach(tasks, t => t.Start());

        await Task.WhenAll(tasks);

        await v1.Set(41);

        Assert.Equal(82, await m1.Get());

        Assert.True(memoInvocationCount >= 1, "Must be invoked at least once");
        Assert.True(invocationCountR1 <= memoInvocationCount, "Must be invoked at least once");
        Assert.True(invocationCountR2 <= memoInvocationCount, "Must be invoked at least once");
    }

    [RetryFact(3, 1000)]
    public async Task TestThreadSafety6()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);

        var memoInvocationCount = 0;
        var m1 = f.CreateMemoizR("m1", async () =>
        {
            memoInvocationCount++;
            return await v1.Get() * 2;
        });

        var m2 = f.CreateMemoizR("m2", async () =>
        {
            await Task.Delay(100);
            return await m1.Get() * 2;
        });

        var m3 = f.CreateMemoizR("m3", async () =>
        {
            await Task.Delay(100);
            return await m1.Get() * 2;
        });

        var result1 = 0;
        var r1 = f.BuildReaction().CreateReaction(m2, m => result1 = m);
        var result2 = 0;
        var r2 = f.BuildReaction().CreateReaction(m3, m => result2 = m);

        var tasks = new List<Task>
        {
            Task.Run(async () => await v1.Set(41))
        };

        await Task.Delay(500);

        tasks.Add(Task.Run(async () => await v1.Set(42)));

        await Task.WhenAll(tasks);

        await Task.Delay(190);

        Assert.Equal(168, result1);
        Assert.Equal(168, result2);
    }

    // Resume() recomputes the reaction under the node mutex + ContextLock like the debounced
    // update path, so running it concurrently with a flood of Set() calls must serialize cleanly:
    // no exception, no deadlock, and the reaction must still converge on the final write. The
    // node mutex is what orders Resume against the concurrently scheduled debounced updates --
    // without it, a stale in-flight Execute could apply its side effects after the newest update
    // finished, and the reaction would settle on the wrong value with nothing re-running it.
    [Fact(Timeout = 10000)]
    public async Task Reaction_ResumeConcurrentWithSet_DoesNotThrowAndConverges()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(0);
        var last = -1;
        var r = f.BuildReaction().CreateReaction(v1, v => last = v);

        r.Pause();

        // Hammer Set from many tasks while Resume runs concurrently against the same context.
        var setters = Enumerable.Range(1, 200).Select(i => Task.Run(() => v1.Set(i))).ToArray();
        var resume = r.Resume();
        await Task.WhenAll(setters);
        await resume;          // must not throw / deadlock
        await v1.Set(1000);

        // The debounced reaction must settle on the last write.
        await TestHelpers.WaitForConvergenceAsync(() => last == 1000);

        Assert.Equal(1000, last);
        GC.KeepAlive(r);
    }

    // Resume() called from inside an active evaluation (here: a memo's fn) must not tear down
    // the flow's ReactionScope -- it did not create it. With the old unconditional CleanScope,
    // the enclosing memo's dependency capture silently resolved to a fresh empty scope, its
    // Sources were never wired, and later Sets never invalidated it.
    [Fact(Timeout = 10000)]
    public async Task Reaction_ResumeInsideActiveEvaluation_DoesNotDestroyEnclosingScope()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var r = f.BuildReaction().CreateReaction(v1, _ => { });
        r.Pause();

        var m = f.CreateMemoizR(async () =>
        {
            var x = await v1.Get();
            await r.Resume();
            return x * 2;
        });

        Assert.Equal(2, await m.Get());
        await v1.Set(5);
        // Dependency tracking must have survived the nested Resume: the Set must dirty m.
        Assert.Equal(10, await m.Get());
        GC.KeepAlive(r);
    }

    // A test SynchronizationContext that runs posted callbacks on the thread pool, makes itself
    // Current for the callback (so async-void exceptions are rethrown through it, exactly like a
    // UI context), and records any exception a callback throws instead of crashing the test host.
    private sealed class CapturingSynchronizationContext : SynchronizationContext
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<Exception> Exceptions = new();
        private int posted;
        public int Posted => Volatile.Read(ref posted);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref posted);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var prev = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                catch (Exception e)
                {
                    Exceptions.Enqueue(e);
                }
                finally
                {
                    SetSynchronizationContext(prev);
                }
            });
        }
    }

    // Happy path for the SynchronizationContext marshalling: Execute must actually be posted to
    // the supplied context and the reaction must converge through it.
    [Fact(Timeout = 10000)]
    public async Task Reaction_WithSynchronizationContext_ExecutesViaContextAndConverges()
    {
        var syncCtx = new CapturingSynchronizationContext();
        var f = new MemoFactory().AddSynchronizationContext(syncCtx);
        var v1 = f.CreateSignal(1);
        var last = 0;
        var r = f.BuildReaction().CreateReaction(v1, v => last = v);

        await TestHelpers.WaitForConvergenceAsync(() => last == 1);
        Assert.Equal(1, last);
        Assert.True(syncCtx.Posted > 0, "Execute was never posted to the supplied SynchronizationContext");

        await v1.Set(7);
        await TestHelpers.WaitForConvergenceAsync(() => last == 7);
        Assert.Equal(7, last);
        Assert.True(syncCtx.Exceptions.IsEmpty,
            $"unhandled exception escaped onto the SynchronizationContext: {string.Join(", ", syncCtx.Exceptions)}");
        GC.KeepAlive(r);
    }

    // Regression for the async-void double completion in InvokeExecute's posted callback: when
    // Execute throws, the TCS was completed with SetException and then SetResult, and the
    // resulting InvalidOperationException escaped the async void onto the SynchronizationContext
    // -- a process crash on a real UI context. The fault must instead surface exactly once,
    // through the awaited update (observable via Resume), with nothing escaping onto the context,
    // and the reaction must recover on the next Set.
    [Fact(Timeout = 10000)]
    public async Task Reaction_ExecuteThrowsUnderSynchronizationContext_FaultsResumeWithoutCrashingContext()
    {
        var syncCtx = new CapturingSynchronizationContext();
        var f = new MemoFactory().AddSynchronizationContext(syncCtx);
        var v1 = f.CreateSignal(1);
        var last = 0;
        var r = f.BuildReaction().CreateReaction(v1, v =>
        {
            if (v == 13) throw new InvalidOperationException("boom13");
            last = v;
        });

        await TestHelpers.WaitForConvergenceAsync(() => last == 1); // initial run done

        r.Pause();
        await v1.Set(13);
        // Resume runs the pending update inline, so the Execute fault propagates to the caller.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Resume());
        Assert.Contains("boom13", ex.Message);

        // The failed run must not have poisoned the reaction: the next write still triggers it.
        await v1.Set(2);
        await TestHelpers.WaitForConvergenceAsync(() => last == 2);
        Assert.Equal(2, last);

        // With the double-completion bug, the posted async void rethrew
        // InvalidOperationException("...already completed...") through the context.
        Assert.True(syncCtx.Exceptions.IsEmpty,
            $"unhandled exception escaped onto the SynchronizationContext: {string.Join(", ", syncCtx.Exceptions)}");
        GC.KeepAlive(r);
    }

    // The node mutex must serialize a reaction's updates: two debounced updates inherit
    // *different* flows' ContextLocks (which therefore order nothing between them), and the
    // generation guard protects only the State commit, not Execute's side effects. Overlapping
    // Executes would let a stale run apply its effects after a newer one finished.
    [Fact(Timeout = 20000)]
    public async Task Reaction_ExecutionsNeverOverlap_UnderConcurrentSets()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(0);
        var running = 0;
        var maxRunning = 0;
        var last = -1;
        var r = f.BuildReaction().CreateReaction(v1, v =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref maxRunning)) < now)
            {
                Interlocked.CompareExchange(ref maxRunning, now, seen);
            }
            Thread.Sleep(5); // widen the overlap window
            last = v;
            Interlocked.Decrement(ref running);
        });

        await TestHelpers.WaitForConvergenceAsync(() => last == 0); // initial run done

        var setters = Enumerable.Range(1, 50).Select(i => Task.Run(() => v1.Set(i))).ToArray();
        await Task.WhenAll(setters);
        await v1.Set(1000);

        await TestHelpers.WaitForConvergenceAsync(() => last == 1000);
        Assert.Equal(1000, last);
        Assert.Equal(1, maxRunning); // executions ran, and never two at once
        GC.KeepAlive(r);
    }

    // Pause/Resume contract: a paused reaction must not execute for writes that arrive while
    // paused, and Resume must run the pending update inline (so the latest value is applied by
    // the time Resume's task completes).
    [Fact(Timeout = 10000)]
    public async Task Reaction_PausedDoesNotRun_ResumeRunsPendingInline()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var last = -1;
        var r = f.BuildReaction().CreateReaction(v1, v => last = v);

        await TestHelpers.WaitForConvergenceAsync(() => last == 1); // initial run done

        r.Pause();
        await v1.Set(5);
        await Task.Delay(100); // give the (paused) debounced update every chance to run wrongly
        Assert.Equal(1, last); // paused: must not have executed

        await r.Resume();
        Assert.Equal(5, last); // Resume ran the pending update before returning
        GC.KeepAlive(r);
    }
}