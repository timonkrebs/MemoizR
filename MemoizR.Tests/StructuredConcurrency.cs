using Xunit.Sdk;

namespace MemoizR.Tests;

public class StructuredConcurrency
{
    [Fact]
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
    public void TestExceptionHandling()
    {
        var f = new MemoFactory("concurrent");

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
                throw new SkipException("Test");
            });

        Assert.Throws<AggregateException>(() => c1.Get().GetAwaiter().GetResult());
    }

    [Fact(Timeout = 1000)]
    public async Task TestRaceHandling()
    {
        var f = new MemoFactory("concurrent");

        // all tasks get canceled if one fails
        var c1 = f.CreateConcurrentRace(
            async c =>
            {
                await Task.Delay(100, c.Token);
                return 1;
            },
            async c =>
            {
                await Task.Delay(3000, c.Token);
                return 2;
            },
            async c =>
            {
                await Task.Delay(5000, c.Token);
                return 3;
            });

        Assert.Equal(1, await c1.Get());
    }

    [Fact]
    public async Task TestMultipleMapHandling()
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

        await Task.Delay(100);
        Assert.Equal(1, invocations);

        await v1.Set(4);
        await Task.Delay(100);
        Assert.Equal(4, x.Single(x => x == 4));
        Assert.Equal(4, x.ElementAt(0));
        Assert.Equal(2, invocations);

        await v2.Set(5);
        await Task.Delay(100);
        Assert.Equal(5, x.Single(x => x == 5));
        Assert.Equal(5, x.ElementAt(1));
        Assert.Equal(3, invocations);

        await v3.Set(6);
        await Task.Delay(100);
        Assert.Equal(6, x.Single(x => x == 6));
        Assert.Equal(6, x.ElementAt(2));
        Assert.Equal(4, invocations);

        // If canceled nothing should change
        await v2.Set(7);
        await v2.Set(6);
        c1.Cancel();
        await Task.Delay(100);
        Assert.Equal(5, x.Single(x => x == 5));
        Assert.Equal(5, x.ElementAt(1));
        Assert.Equal(5, invocations);

        // cancellation should not disable reactivity and never get into race conditions
        await v2.Set(7);
        await v2.Set(8);
        await Task.Delay(100);
        Assert.Equal(8, x.Single(x => x == 8));
        Assert.Equal(8, x.ElementAt(1));
        Assert.Equal(6, invocations);
    }

    [Fact(Timeout = 1000)]
    public async Task TestThreadSafety()
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
                await Task.Delay(50, c.Token);
                return await v3.Get();
            });

        var x = await c1.Get();
        f.BuildReaction().CreateReaction(c1, c => x = c);

        await Task.Delay(100);
        var _ = v1.Set(1);

        for (var i = 0; i < 100; i++)
        {
            _ = v1.Set(i);
            _ = v2.Set(i);
            _ = v3.Set(i);
        }

        _ = v1.Set(1);
        _ = v2.Set(2);
        await v3.Set(3);
        await Task.Delay(100);
        Assert.Equal(1, x.ElementAt(0));
        Assert.Equal(2, x.ElementAt(1));
        Assert.Equal(3, x.ElementAt(2));

        await v1.Set(4);
        await Task.Delay(100);
        Assert.Equal(4, x.Single(x => x == 4));
        Assert.Equal(4, x.ElementAt(0));
        Assert.Equal(2, x.ElementAt(1));
        Assert.Equal(3, x.ElementAt(2));

        await v2.Set(5);
        await Task.Delay(100);
        Assert.Equal(5, x.Single(x => x == 5));
        Assert.Equal(4, x.ElementAt(0));
        Assert.Equal(5, x.ElementAt(1));
        Assert.Equal(3, x.ElementAt(2));

        await v3.Set(6);
        await Task.Delay(100);
        Assert.Equal(6, x.Single(x => x == 6));
        Assert.Equal(6, x.ElementAt(2));
    }

    [Fact(Timeout = 1000)]
    public async Task TestChildExecptionCancelationHandling()
    {
        var f = new MemoFactory("concurrent");

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
                await child1.Get(c);
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

    [Fact(Skip = "to long")]
    public async Task TestChildExecutionHandling()
    {
        var f = new MemoFactory("concurrent");

        var child1 = f.CreateConcurrentMapReduce(
            async c =>
            {
                await Task.Delay(3000);
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
                await Task.Delay(2000, c.Token);
                return 2;
            });

        var x = await c1.Get(); // should take 3 seconds
    }
}
