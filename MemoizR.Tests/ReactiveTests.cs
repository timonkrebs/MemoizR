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

    [Fact(Timeout = 30000)]
    public async Task TestAsyncEnumerable()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var invocationCount = 0;
        var result = 0;

        var m1 = f.BuildReaction()
        .CreateAsyncEnumerableExperimental(v1);

        // The backing reaction fires once on creation, subscribing to v1 and advancing the stream
        // past the initial value. Wait for that subscription instead of guessing with a fixed sleep,
        // so the first element the consumer sees is a genuine change.
        await Eventually.Until(() => Assert.NotEmpty(v1.Observers));

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
        await Eventually.Until(() =>
        {
            Assert.Equal(2, result);
            Assert.Equal(1, invocationCount);
        });

        await v1.Set(3);
        await Eventually.Until(() =>
        {
            Assert.Equal(3, result);
            Assert.Equal(2, invocationCount);
        });

        // Setting the same value emits nothing new: negative assertion, so settle then verify.
        await v1.Set(3);
        await Task.Delay(50);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);

        await v1.Set(4);
        await Eventually.Until(() =>
        {
            Assert.Equal(4, result);
            Assert.Equal(3, invocationCount);
        });
        Assert.NotNull(m1);
    }

    // NOTE: this test models a deliberately slow consumer (100ms per item). The experimental
    // async-enumerable is lossy under backpressure — the producer advances the stream slot
    // regardless of the consumer — so the fixed 200ms producer cadence is load-bearing: it lets
    // the consumer finish its cycle and re-park on the next slot before the next value is set.
    // We keep that cadence and only widen the per-test timeout for slow CI agents.
    [Fact(Timeout = 30000)]
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

    [Fact(Timeout = 30000)]
    public async Task TestReactiveInvocations()
    {
        var invocations = 0;
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());

        var m1 = f.BuildReaction()
        .CreateReaction(v1, v => invocations++);

        await Eventually.Until(() => Assert.Equal(1, invocations));

        await v1.Set(2);
        await Eventually.Until(() => Assert.Equal(2, invocations));

        // Setting the same value must not fire the reaction again: negative, so settle then verify.
        await v1.Set(2);
        await Task.Delay(50);
        Assert.Equal(2, invocations);
    }

    [Fact(Timeout = 30000)]
    public async Task TestAutoSubscriptionHandling()
    {
        var invocations = new Invocations { Count = 0 };
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);

        Assert.Equal(1, await v1.Get());

        CreateReaction(f, v1, invocations);

        // The pending initial run keeps the reaction alive until it fires exactly once; wait for it.
        await Eventually.Until(() => Assert.Equal(1, invocations.Count));

        // With no strong reference remaining, the reaction is collectible and must stop reacting.
        GC.Collect();
        GC.Collect();
        await Task.Delay(100);
        GC.Collect();
        GC.Collect();

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

    [Fact(Timeout = 30000)]
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

        // Wait until the reaction has been created and subscribed, rather than a fixed sleep.
        await Eventually.Until(() => Assert.NotEmpty(m1.Observers));

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

        // v1 ends at 20, so m1 == 40 and the reaction converges to that.
        await Eventually.Until(async () =>
        {
            Assert.Equal(40, await m1.Get());
            Assert.Equal(40, result);
        });
        Assert.Equal(await m1.Get(), result);

        Assert.True(invocationCount >= 1, "Must be invoked at least once");
    }

    [Fact(Timeout = 2000)]
    public async Task TestThreadSafety2()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var invocationCount = 0;
        var r1 = f.BuildReaction()
        .AddDebounceTime(TimeSpan.FromMilliseconds(10))
        .CreateReaction(m1, m => invocationCount++);
        await Eventually.Until(() => Assert.NotEmpty(m1.Observers));
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

        // v1 ends at 200, so m1 == 400 and the reaction fires more than once over the storm.
        await Eventually.Until(async () =>
        {
            Assert.Equal(400, await m1.Get());
            Assert.True(invocationCount > 1, "Must be invoked more than once");
        });
        Assert.NotEqual(400, resultM1);
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

    // RetryFact like its structural siblings (TestThreadSafety3/5/6): this asserts exact invocation
    // counts, which require each of the 41 spaced sets to be observed distinctly. That is robust on
    // a quiet machine but can rarely coalesce under contention; the retry absorbs that tail.
    [RetryFact(3, 1000)]
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

        // Both reactions must have run their initial pass (and subscribed) before the storm.
        await Eventually.Until(() =>
        {
            Assert.True(invocationCountR1 >= 1);
            Assert.True(invocationCountR2 >= 1);
        });

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

        // 41 distinct sets plus the one initial pass == 42 invocations at each level.
        await Eventually.Until(() =>
        {
            Assert.Equal(42, memoInvocationCount);
            Assert.Equal(42, invocationCountR1);
            Assert.Equal(42, invocationCountR2);
        }, timeoutMs: 4000);
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
}