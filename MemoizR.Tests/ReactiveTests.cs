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

        await TestHelpers.WaitForConvergenceAsync(() => invocations == 1);
        Assert.Equal(1, invocations);

        await v1.Set(2);
        await TestHelpers.WaitForConvergenceAsync(() => invocations == 2);
        Assert.Equal(2, invocations);

        // Same-value Set must not retrigger: a negative assertion, so it keeps a real
        // quiescence window instead of a poll.
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

        // The reaction is built on another task; wait for its initial run instead of guessing.
        await TestHelpers.WaitForConvergenceAsync(() => invocationCount >= 1);

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

        // The fire-and-forget Sets race each other; the last write of the second loop (20) must
        // win once everything settles -- poll for the debounced reaction to land on it.
        await TestHelpers.WaitForConvergenceAsync(() => result == 40);

        Assert.Equal(40, await m1.Get());
        Assert.Equal(40, result);
        Assert.Equal(await m1.Get(), result);
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
            // Pace the Sets so they spread over time (each lands well outside the 1ms debounce
            // window); 10ms keeps that property without the original 35ms * 40 = 1.4s of padding.
            await Task.Delay(10);
        }
        await Task.WhenAll(tasks);
        tasks.Add(Task.Run(async () => await v1.Set(41)));

        await Task.Delay(1);
        var resultM1 = 0;
        tasks.Add(Task.Run(async () => resultM1 = await m1.Get()));

        await Task.WhenAll(tasks);
        // Wait for both debounced reactions to land on the final write instead of a fixed delay.
        await TestHelpers.WaitForConvergenceAsync(() => result1 == 82 && result2 == 82);

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

        // Wait for the first write to propagate through both slow memos (100ms each) before the
        // second write, instead of a fixed 500ms: 41 * 2 * 2 = 164.
        await TestHelpers.WaitForConvergenceAsync(() => result1 == 164 && result2 == 164);

        tasks.Add(Task.Run(async () => await v1.Set(42)));

        await Task.WhenAll(tasks);

        await TestHelpers.WaitForConvergenceAsync(() => result1 == 168 && result2 == 168);

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

    // Happy path for the SynchronizationContext marshalling: the reaction's action must actually
    // be posted to the supplied context and the reaction must converge through it. Generous poll
    // budgets: the action round-trips through the context's Post (a thread-pool hop), so under
    // full-suite parallel load on a small runner propagation can take a while -- the polls exit
    // early on healthy runs.
    [Fact(Timeout = 30000)]
    public async Task Reaction_WithSynchronizationContext_RunsActionViaContextAndConverges()
    {
        var syncCtx = new CapturingSynchronizationContext();
        var f = new MemoFactory().AddSynchronizationContext(syncCtx);
        var v1 = f.CreateSignal(1);
        var last = 0;
        var r = f.BuildReaction().CreateReaction(v1, v => last = v);

        await TestHelpers.WaitForConvergenceAsync(() => last == 1, timeoutMs: 15000);
        Assert.Equal(1, last);
        Assert.True(syncCtx.Posted > 0, "the action was never posted to the supplied SynchronizationContext");

        await v1.Set(7);
        await TestHelpers.WaitForConvergenceAsync(() => last == 7, timeoutMs: 15000);
        Assert.Equal(7, last);
        Assert.True(syncCtx.Exceptions.IsEmpty,
            $"unhandled exception escaped onto the SynchronizationContext: {string.Join(", ", syncCtx.Exceptions)}");
        GC.KeepAlive(r);
    }

    // The thread-pool/UI-thread split (#13): with a SynchronizationContext registered, dependency
    // evaluation (the memo computations) must stay OFF the context -- on the worker flow that
    // runs the update -- and only the action, with the already-evaluated values, may be
    // marshalled to it. The capturing context makes itself Current for posted callbacks, so
    // SynchronizationContext.Current distinguishes the two sides.
    [Fact(Timeout = 30000)]
    public async Task Reaction_WithSynchronizationContext_EvaluatesDependenciesOffContext_RunsActionOnContext()
    {
        var syncCtx = new CapturingSynchronizationContext();
        var f = new MemoFactory().AddSynchronizationContext(syncCtx);
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(10);
        var memoRanOnContext = false;
        var m1 = f.CreateMemoizR(async () =>
        {
            memoRanOnContext |= SynchronizationContext.Current == syncCtx;
            return await v1.Get() * 2;
        });

        var last = 0;
        var actionRanOffContext = false;
        var r = f.BuildReaction().CreateReaction(m1, v2, (a, b) =>
        {
            actionRanOffContext |= SynchronizationContext.Current != syncCtx;
            last = a + b;
        });

        await TestHelpers.WaitForConvergenceAsync(() => last == 12, timeoutMs: 15000);

        await v1.Set(5);
        await TestHelpers.WaitForConvergenceAsync(() => last == 20, timeoutMs: 15000);

        Assert.False(memoRanOnContext, "dependency evaluation ran via the SynchronizationContext; it must stay on the thread pool");
        Assert.False(actionRanOffContext, "the action ran outside the SynchronizationContext");
        Assert.True(syncCtx.Exceptions.IsEmpty,
            $"unhandled exception escaped onto the SynchronizationContext: {string.Join(", ", syncCtx.Exceptions)}");
        GC.KeepAlive(r);
    }

    // CreateAdvancedReaction is the escape hatch from the dependency/action split: its opaque
    // Func<Task> body cannot be split, so the WHOLE body still starts on the context
    // (ReactionBase.InvokeExecute). A fault must surface exactly once through the awaited update
    // (observable via Resume) with nothing escaping onto the context -- this keeps the
    // async-void double-completion regression covered now that plain reactions no longer go
    // through InvokeExecute's posted callback.
    [Fact(Timeout = 30000)]
    public async Task AdvancedReaction_WithSynchronizationContext_RunsWholeBodyViaContext_AndFaultsCleanly()
    {
        var syncCtx = new CapturingSynchronizationContext();
        var f = new MemoFactory().AddSynchronizationContext(syncCtx);
        var v1 = f.CreateSignal(1);
        var last = 0;
        var bodyStartedOffContext = false;
        var r = f.BuildReaction().CreateAdvancedReaction(async () =>
        {
            bodyStartedOffContext |= SynchronizationContext.Current != syncCtx;
            var v = await v1.Get();
            if (v == 13) throw new InvalidOperationException("boom13");
            last = v;
        });

        await TestHelpers.WaitForConvergenceAsync(() => last == 1, timeoutMs: 15000);
        Assert.False(bodyStartedOffContext, "the advanced reaction body must start on the SynchronizationContext");
        Assert.True(syncCtx.Posted > 0, "the body was never posted to the supplied SynchronizationContext");

        r.Pause();
        await v1.Set(13);
        // Resume runs the pending update inline, so the Execute fault propagates to the caller.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Resume());
        Assert.Contains("boom13", ex.Message);

        // The failed run must not have poisoned the reaction: the next write still triggers it.
        await v1.Set(2);
        await TestHelpers.WaitForConvergenceAsync(() => last == 2, timeoutMs: 15000);
        Assert.Equal(2, last);

        Assert.True(syncCtx.Exceptions.IsEmpty,
            $"unhandled exception escaped onto the SynchronizationContext: {string.Join(", ", syncCtx.Exceptions)}");
        GC.KeepAlive(r);
    }

    // A throwing action under a SynchronizationContext (the posted callback is
    // ReactionBuilder.InvokeActionAsync's): the fault must surface exactly once, through the
    // awaited update (observable via Resume), with nothing escaping onto the context -- on a
    // real UI context an escaped exception is a process crash -- and the reaction must recover
    // on the next Set. (The async-void double-completion regression that motivated this shape
    // lives on InvokeExecute's posted callback, covered by the AdvancedReaction test above.)
    [Fact(Timeout = 30000)]
    public async Task Reaction_ActionThrowsUnderSynchronizationContext_FaultsResumeWithoutCrashingContext()
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

        await TestHelpers.WaitForConvergenceAsync(() => last == 1, timeoutMs: 15000); // initial run done

        r.Pause();
        await v1.Set(13);
        // Resume runs the pending update inline, so the Execute fault propagates to the caller.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Resume());
        Assert.Contains("boom13", ex.Message);

        // The failed run must not have poisoned the reaction: the next write still triggers it.
        await v1.Set(2);
        await TestHelpers.WaitForConvergenceAsync(() => last == 2, timeoutMs: 15000);
        Assert.Equal(2, last);

        // With the double-completion bug, the posted async void rethrew
        // InvalidOperationException("...already completed...") through the context.
        Assert.True(syncCtx.Exceptions.IsEmpty,
            $"unhandled exception escaped onto the SynchronizationContext: {string.Join(", ", syncCtx.Exceptions)}");
        GC.KeepAlive(r);
    }

    // Creating a reaction must not run its eager initial evaluation inline on the creating
    // thread: reactions are typically created on UI threads (see MemoizR.Wpf), where the
    // dependency evaluation belongs on the thread pool even for the initial run. The
    // zero-debounce initial Stale used to continue inline off the already-completed
    // Task.Delay(0) (ConfigureAwait(false) does not yield on completed tasks), evaluating the
    // whole graph on the creating thread before CreateReaction returned -- pinned here by the
    // ForceYielding in RunDebouncedUpdateAsync. (Its companion guarantee -- the yield must not
    // re-queue to the creating thread's SynchronizationContext like Task.Yield would -- needs a
    // real UI context to observe and is covered by MemoizR.Wpf.Tests.)
    [Fact(Timeout = 10000)]
    public async Task Reaction_InitialRun_DoesNotEvaluateDependenciesInlineOnCreatingThread()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var creatingThreadId = Environment.CurrentManagedThreadId;
        var creating = true;
        var ranInlineOnCreatingThread = false;
        var m1 = f.CreateMemoizR(async () =>
        {
            // Same thread AND still inside the creation call: only the inline path can observe
            // both (the creating thread cannot serve queued pool work while it is still inside
            // CreateReaction), so a pool-scheduled run can never trip this flag.
            ranInlineOnCreatingThread |= Environment.CurrentManagedThreadId == creatingThreadId && Volatile.Read(ref creating);
            return await v1.Get();
        });

        var last = 0;
        var r = f.BuildReaction().CreateReaction(m1, v => last = v);
        Volatile.Write(ref creating, false);

        await TestHelpers.WaitForConvergenceAsync(() => last == 1);
        Assert.Equal(1, last);
        Assert.False(ranInlineOnCreatingThread, "the initial run evaluated dependencies inline on the creating thread");
        GC.KeepAlive(r);
    }

    // Separate-parameter dependencies must be evaluated IN PARALLEL (issue #13: "so that they
    // can be evaluated independently"): each runs on its own pinned scope, so a dirty update
    // costs the slowest dependency instead of the sum. The two memos here deadlock unless both
    // computations are in flight at once -- each releases the other's gate before reading its
    // signal -- so a sequential composition times out the gates and never converges.
    [Fact(Timeout = 30000)]
    public async Task Reaction_MultiDependency_EvaluatesDependenciesInParallel()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(2);
        var entered1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var m1 = f.CreateMemoizR(async () =>
        {
            entered1.TrySetResult();
            await entered2.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await v1.Get();
        });
        var m2 = f.CreateMemoizR(async () =>
        {
            entered2.TrySetResult();
            await entered1.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await v2.Get();
        });

        var last = 0;
        var r = f.BuildReaction().CreateReaction(m1, m2, (a, b) => last = a + b);

        await TestHelpers.WaitForConvergenceAsync(() => last == 3, timeoutMs: 15000);
        Assert.Equal(3, last);
        GC.KeepAlive(r);
    }

    // Every parameter is registered (and eagerly subscribed) BEFORE evaluation starts, so a
    // dependency faulting on the reaction's first run leaves ALL parameters wired: a Set on any
    // of them revives the reaction. The sequential composition only wired the prefix read before
    // the fault -- here m1 faults first, so v2 was never wired and its Set was lost forever.
    [Fact(Timeout = 30000)]
    public async Task Reaction_DependencyFaultsOnFirstRun_AllParametersStillWired_AndRecovers()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(10);
        var attempts = 0;
        var shouldThrow = true;
        var m1 = f.CreateMemoizR(async () =>
        {
            Interlocked.Increment(ref attempts);
            if (Volatile.Read(ref shouldThrow)) throw new InvalidOperationException("dep boom");
            return await v1.Get();
        });

        var last = 0;
        var r = f.BuildReaction().CreateReaction(m1, v2, (a, b) => last = a + b);

        // Wait until the initial run has actually attempted (and faulted) before disarming.
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref attempts) >= 1);
        Assert.True(Volatile.Read(ref attempts) >= 1, "the initial run never executed");
        Assert.Equal(0, last); // the faulted run must not have produced the side effect

        Volatile.Write(ref shouldThrow, false);
        await v2.Set(20); // revive via the dependency AFTER the faulting one
        await TestHelpers.WaitForConvergenceAsync(() => last == 21, timeoutMs: 15000);
        Assert.Equal(21, last);
        GC.KeepAlive(r);
    }

    // AddSynchronizationContext re-registration contract (changed when the static side-table
    // became a factory property): a second registration REPLACES the context for reactions
    // built afterwards; reactions built earlier keep the context they captured at build time.
    [Fact(Timeout = 30000)]
    public async Task AddSynchronizationContext_ReRegistration_ReplacesForNewReactions()
    {
        var ctx1 = new CapturingSynchronizationContext();
        var ctx2 = new CapturingSynchronizationContext();
        var f = new MemoFactory().AddSynchronizationContext(ctx1);
        var v1 = f.CreateSignal(1);

        var last1 = 0;
        var r1 = f.BuildReaction().CreateReaction(v1, v => last1 = v);
        await TestHelpers.WaitForConvergenceAsync(() => last1 == 1, timeoutMs: 15000);
        var ctx1PostsAfterR1 = ctx1.Posted;
        Assert.True(ctx1PostsAfterR1 > 0, "r1 must execute via ctx1");

        // Re-register: must replace, not throw (the old static-dictionary version threw on Add).
        f.AddSynchronizationContext(ctx2);

        var last2 = 0;
        var r2 = f.BuildReaction().CreateReaction(v1, v => last2 = v);
        await TestHelpers.WaitForConvergenceAsync(() => last2 == 1, timeoutMs: 15000);
        Assert.True(ctx2.Posted > 0, "r2 must execute via the replacement ctx2");

        // r1 captured ctx1 at build time and keeps using it.
        await v1.Set(7);
        await TestHelpers.WaitForConvergenceAsync(() => last1 == 7 && last2 == 7, timeoutMs: 15000);
        Assert.True(ctx1.Posted > ctx1PostsAfterR1, "r1 must still execute via ctx1 after the re-registration");
        GC.KeepAlive(r1);
        GC.KeepAlive(r2);
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

    // A reaction whose INITIAL run throws must still end up wired to the dependencies it read
    // before throwing. A reaction has no pull path: if the captured links are dropped on the
    // error path, no future Set can ever wake it -- the reaction is orphaned forever.
    [Fact(Timeout = 10000)]
    public async Task Reaction_InitialRunThrows_StillWiresDependencies_AndRecovers()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var last = -1;
        var attempts = 0;
        var shouldThrow = true;
        var r = f.BuildReaction().CreateReaction(v1, v =>
        {
            Interlocked.Increment(ref attempts);
            if (Volatile.Read(ref shouldThrow)) throw new InvalidOperationException("initial boom");
            last = v;
        });

        // Wait until the initial run has ACTUALLY attempted (and thrown) before disarming: a
        // fixed delay raced the fire-and-forget initial run -- when the flip won, the initial run
        // succeeded and the catch-path wiring this test exists to pin never executed at all.
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref attempts) >= 1);
        Assert.True(Volatile.Read(ref attempts) >= 1, "the initial run never executed");
        Assert.Equal(-1, last); // the throwing run must not have produced the side effect

        Volatile.Write(ref shouldThrow, false);
        await v1.Set(5);
        await TestHelpers.WaitForConvergenceAsync(() => last == 5);
        Assert.Equal(5, last);
        GC.KeepAlive(r);
    }

    // Dispose contract: a disposed reaction must be unsubscribed from its sources (no observer
    // entry keeping it in the graph) and must never execute again.
    [Fact(Timeout = 10000)]
    public async Task Reaction_Dispose_StopsTriggeringAndUnsubscribes()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var last = -1;
        var r = f.BuildReaction().CreateReaction(v1, v => last = v);

        await TestHelpers.WaitForConvergenceAsync(() => last == 1); // initial run done

        r.Dispose();
        Assert.False(TestHelpers.Observes(v1.Observers, r), "the disposed reaction is still observing its source");

        await v1.Set(5);
        await Task.Delay(100); // negative assertion: nothing may run -- needs a real window
        Assert.Equal(1, last);
        GC.KeepAlive(r);
    }

    // The multi-source CreateReaction overload family shares one wiring path; pin it with two
    // sources: the reaction triggers on either source and the action sees both current values.
    [Fact(Timeout = 10000)]
    public async Task Reaction_TwoSources_TriggersOnEitherAndSeesBothValues()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(10);
        var sum = -1;
        var r = f.BuildReaction().CreateReaction(v1, v2, (x, y) => sum = x + y);

        await TestHelpers.WaitForConvergenceAsync(() => sum == 11);
        Assert.Equal(11, sum);

        await v1.Set(2);
        await TestHelpers.WaitForConvergenceAsync(() => sum == 12);
        Assert.Equal(12, sum);

        await v2.Set(20);
        await TestHelpers.WaitForConvergenceAsync(() => sum == 22);
        Assert.Equal(22, sum);
        GC.KeepAlive(r);
    }
}