using xRetry;

namespace MemoizR.Tests;

public class StructuredConcurrencyTests
{
    [Fact(Timeout = 1000)]
    public async Task TestInitialization()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());
        Assert.Equal(1, await v1.Get());

        await v1.Set(2);
        Assert.Equal(2, await v1.Get());
        Assert.Equal(2, await v1.Get());

        await v1.Set(3);

        var c1 = f.CreateConcurrentMapReduce(
            (val, agg) => agg - val,
            _ => v1.Get(),
            _ => Task.FromResult(3));

        Assert.Equal(-6, await c1.Get());
        Assert.Equal(-6, await c1.Get());

        var c2 = f.CreateConcurrentMapReduce(
            _ => v1.Get(),
            _ => c1.Get());

        Assert.Equal(-3, await c2.Get());
    }

    [Fact(Timeout = 1000)]
    public async Task TestExceptionHandling()
    {
        var f = new MemoFactory();

        // all tasks get canceled if one fails
        var c1 = f.CreateConcurrentMapReduce(
            _ => throw new Exception(),
            async c =>
            {
                await Task.Delay(3000, c.Token);
                return 4;
            },
            async c =>
            {
                await Task.Delay(5000, c.Token);
                throw new Exception("Test");
            });

        await Assert.ThrowsAsync<AggregateException>(c1.Get);
    }

    [Fact(Timeout = 1000)]
    public async Task TestRaceHandling()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(1);

        // all tasks get canceled if one fails
        var c1 = f.CreateConcurrentRace(
            v1.Get,
            async (c, r)  =>
            {
                await Task.Delay(100, c.Token);
                return r * 1;
            },
            async (c, r) =>
            {
                await Task.Delay(3000, c.Token);
                return r * 2;
            },
            async (c, r) =>
            {
                await Task.Delay(5000, c.Token);
                return r * 3;
            });

        Assert.Equal(1, await c1.Get());
    }

    // Timing-sensitive: asserts on debounced async reactions after fixed delays. Retry like
    // the other flaky reactive tests (xRetry) instead of relying on a knife-edge timeout.
    [RetryFact(3, 200)]
    public async Task TestMultipleMapHandling()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(2);
        var v3 = f.CreateSignal(3);

        // all tasks get canceled if one fails
        var c1 = f.CreateConcurrentMap(
            async c =>
            {
                var r = await v1.Get();
                await Task.Delay(2, c.Token);
                return r;
            },
            async c =>
            {
                var r = await v2.Get();
                await Task.Delay(2, c.Token);
                return r;
            },
            async c =>
            {
                var r = await v3.Get();
                await Task.Delay(20, c.Token);
                return r;
            });

        var invocations = 0;
        var x = await c1.Get();
        var r = f.BuildReaction().CreateReaction(c1, c =>
        {
            invocations++;
            x = c;
        });

        // Poll for the propagated value (the reaction assigns x after bumping invocations, so a
        // converged value implies the matching invocation completed) instead of fixed delays.
        await WaitForConvergenceAsync(() => invocations == 1);
        Assert.Equal(1, invocations);

        await v1.Set(4);
        await WaitForConvergenceAsync(() => x.ElementAt(0) == 4);
        Assert.NotNull(r);
        Assert.Equal(4, x.Single(x => x == 4));
        Assert.Equal(4, x.ElementAt(0));
        Assert.Equal(2, invocations);

        await v2.Set(5);
        await WaitForConvergenceAsync(() => x.ElementAt(1) == 5);
        Assert.Equal(5, x.Single(x => x == 5));
        Assert.Equal(5, x.ElementAt(1));
        Assert.Equal(3, invocations);

        await v3.Set(6);
        await WaitForConvergenceAsync(() => x.ElementAt(2) == 6);
        Assert.Equal(6, x.Single(x => x == 6));
        Assert.Equal(6, x.ElementAt(2));
        Assert.Equal(4, invocations);

        await v2.Set(7);
        await v2.Set(8);
        await WaitForConvergenceAsync(() => x.ElementAt(1) == 8);
        Assert.Equal(8, x.Single(x => x == 8));
        Assert.Equal(8, x.ElementAt(1));
        Assert.Equal(5, invocations);

        // Re-setting to the same final value must not retrigger: this is a negative assertion,
        // so it needs a real quiescence window, not a poll.
        await v2.Set(7);
        await v2.Set(8);
        await Task.Delay(100);
        Assert.Equal(8, x.Single(x => x == 8));
        Assert.Equal(8, x.ElementAt(1));
        Assert.Equal(5, invocations);
        Assert.NotNull(r);
    }

    [RetryFact(3, 200)]
    public async Task TestMultipleMapHandlingCancel()
    {
        var f = new MemoFactory("concurrent");
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(2);
        var v3 = f.CreateSignal(3);

        // all tasks get canceled if one fails
        var c1 = f.CreateConcurrentMap(
            async c =>
            {
                await Task.Delay(2, c.Token);
                return await v1.Get();
            },
            async c =>
            {
                await Task.Delay(2, c.Token);
                return await v2.Get();
            },
            async c =>
            {
                await Task.Delay(20, c.Token);
                return await v3.Get();
            });

        var invocations = 0;
        var x = await c1.Get();
        f.BuildReaction().CreateReaction(c1, c =>
        {
            invocations++;
            x = c;
        });

        // Poll for the propagated value (the reaction assigns x after bumping invocations, so a
        // converged value implies the matching invocation completed) instead of fixed delays.
        await WaitForConvergenceAsync(() => invocations == 1);
        Assert.Equal(1, invocations);

        await v1.Set(4);
        await WaitForConvergenceAsync(() => x.ElementAt(0) == 4);
        Assert.Equal(4, x.Single(x => x == 4));
        Assert.Equal(4, x.ElementAt(0));
        Assert.Equal(2, invocations);

        await v2.Set(5);
        await WaitForConvergenceAsync(() => x.ElementAt(1) == 5);
        Assert.Equal(5, x.Single(x => x == 5));
        Assert.Equal(5, x.ElementAt(1));
        Assert.Equal(3, invocations);

        await v3.Set(6);
        await WaitForConvergenceAsync(() => x.ElementAt(2) == 6);
        Assert.Equal(6, x.Single(x => x == 6));
        Assert.Equal(6, x.ElementAt(2));
        Assert.Equal(4, invocations);

        // If canceled nothing should change -- a negative assertion, so it needs a real
        // quiescence window, not a poll.
        await v2.Set(7);
        await v2.Set(6);
        c1.Cancel();
        await Task.Delay(100);
        Assert.Equal(5, x.Single(x => x == 5));
        Assert.Equal(5, x.ElementAt(1));
        Assert.Equal(4, invocations);

        // cancellation should not disable reactivity and never get into race conditions
        await v2.Set(7);
        await v2.Set(8);
        await WaitForConvergenceAsync(() => x.ElementAt(1) == 8);
        Assert.Equal(8, x.Single(x => x == 8));
        Assert.Equal(8, x.ElementAt(1));
        Assert.Equal(5, invocations);
    }

    [RetryFact(3, 200)]
    public async Task TestThreadSafety()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(2);
        var v3 = f.CreateSignal(3);

        // all tasks get canceled if one fails
        var c1 = f.CreateConcurrentMap(
            async c =>
            {
                await Task.Delay(2, c.Token);
                return await v1.Get();
            },
            async c =>
            {
                await Task.Delay(2, c.Token);
                return await v2.Get();
            },
            async c =>
            {
                await Task.Delay(50, c.Token);
                return await v3.Get();
            });

        var x = await c1.Get();
        f.BuildReaction().CreateReaction(c1, c => x = c);

        await Task.Delay(100);

        // Fire all Sets concurrently (they start hot), then await their completion instead of
        // guessing a fixed settle delay -- same concurrency, deterministic synchronization.
        var concurrentSets = new List<Task> { v1.Set(1) };
        for (var i = 0; i < 100; i++)
        {
            concurrentSets.Add(v1.Set(i));
            concurrentSets.Add(v2.Set(i));
            concurrentSets.Add(v3.Set(i));
        }
        await Task.WhenAll(concurrentSets);

        var one = v1.Set(1);
        var two = v2.Set(2);
        await v3.Set(3);
        // Reactive propagation through the ConcurrentMap is async and debounced (and each map fn
        // sleeps up to 50ms), so wait for the reaction to converge rather than guessing a fixed
        // delay. The map result preserves source order [v1, v2, v3].
        await WaitForConvergenceAsync(() => x.SequenceEqual([1, 2, 3]));
        Assert.Equal(1, x.ElementAt(0));
        Assert.Equal(2, x.ElementAt(1));
        Assert.Equal(3, x.ElementAt(2));

        await v1.Set(4);
        await WaitForConvergenceAsync(() => x.SequenceEqual([4, 2, 3]));
        Assert.Equal(4, x.Single(x => x == 4));
        Assert.Equal(4, x.ElementAt(0));
        Assert.Equal(2, x.ElementAt(1));
        Assert.Equal(3, x.ElementAt(2));

        await v2.Set(5);
        await WaitForConvergenceAsync(() => x.SequenceEqual([4, 5, 3]));
        Assert.Equal(5, x.Single(x => x == 5));
        Assert.Equal(4, x.ElementAt(0));
        Assert.Equal(5, x.ElementAt(1));
        Assert.Equal(3, x.ElementAt(2));

        await v3.Set(6);
        await WaitForConvergenceAsync(() => x.SequenceEqual([4, 5, 6]));
        Assert.Equal(6, x.Single(x => x == 6));
        Assert.Equal(6, x.ElementAt(2));
    }

    private static Task WaitForConvergenceAsync(Func<bool> converged, int timeoutMs = 5000)
        => TestHelpers.WaitForConvergenceAsync(converged, timeoutMs);

    // Dependency tracking through the reduce job's *parallel* children: unlike the results job,
    // they share the parent flow's scope, and the sources they read are accumulated across
    // concurrently running tasks (CurrentGets under Context.Lock, folded under the job Lock --
    // the very path that needs the volatile ReactionScope fields). Every signal read by any
    // child must end up observed, so a later Set on either re-dirties the node.
    [Fact(Timeout = 5000)]
    public async Task ConcurrentMapReduce_ObservesSignalsReadByParallelChildren()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(10);
        var sum = f.CreateConcurrentMapReduce(
            async _ => await v1.Get(),
            async _ => await v2.Get());

        Assert.Equal(11, await sum.Get());

        await v1.Set(2);
        Assert.Equal(12, await sum.Get());

        await v2.Set(20);
        Assert.Equal(22, await sum.Get());
    }

    [Fact(Timeout = 1000)]
    public async Task TestChildExecptionCancelationHandling()
    {
        var f = new MemoFactory();

        var child1 = f.CreateConcurrentMapReduce(
            async c =>
            {
                await Task.Delay(2000, c.Token);
                return 3;
            });

        // all tasks get canceled if one fails
        var c1 = f.CreateConcurrentMapReduce(
            async c =>
            {
                await child1.Get();
                return 4;
            },
            async c =>
            {
                await Task.Delay(5000, c.Token);
                return 2;
            },
            c =>
            {
                throw new Exception();
            });

        await Assert.ThrowsAsync<AggregateException>(c1.Get);
    }

    [Fact(Timeout = 4000)]
    public async Task TestChildExecutionHandling()
    {
        var f = new MemoFactory();

        var child1 = f.CreateConcurrentMapReduce(
            async c =>
            {
                await Task.Delay(300);
                return 3;
            });

        var c1 = f.CreateConcurrentMapReduce(
            async c =>
            {
                await child1.Get();
                return 4;
            },
            async c =>
            {
                // Shorter than the child's delay, so the parent still has to keep waiting for
                // the slower child after this sibling completed (the scenario under test).
                await Task.Delay(200, c.Token);
                return 2;
            });

        var x = await c1.Get(); // must wait for the slowest child (~300ms), not deadlock or cancel
    }
}
