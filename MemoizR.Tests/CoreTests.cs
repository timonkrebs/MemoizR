namespace MemoizR.Tests;

public class CoreTests
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

        var m1 = f.CreateMemoizR(() => Task.FromResult(3));

        Assert.Equal(3, await m1.Get());
        Assert.Equal(3, await m1.Get());
    }

    [Fact]
    public async Task TestComputed()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());

        var m1 = f.CreateMemoizR(async () => await v1.Get());
        Assert.Equal(1, await m1.Get());
        Assert.Equal(1, await m1.Get());

        await v1.Set(2);

        Assert.Equal(2, await m1.Get());
        Assert.Equal(2, await m1.Get());
        Assert.Equal(2, await v1.Get());

        await v1.Set(4);

        Assert.Equal(4, await m1.Get());
        Assert.Equal(4, await m1.Get());
        Assert.Equal(4, await v1.Get());
    }

    [Fact]
    public async Task TestComputedInvocations()
    {
        var invocations = 0;
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());

        var m1 = f.CreateMemoizR(async () =>
        {
            invocations++;
            return await v1.Get();
        });

        await v1.Set(2);

        Assert.Equal(0, invocations);
        await m1.Get();
        Assert.Equal(1, invocations);
        await m1.Get();
        Assert.Equal(1, invocations);
        await m1.Get();
        Assert.Equal(1, invocations);
        await m1.Get();
    }

    [Fact]
    public async Task TestDiamond()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal("v1", 1);
        Assert.Equal(1, await v1.Get());

        var m1 = f.CreateMemoizR("m1", async () => await v1.Get());
        Assert.Equal(1, await m1.Get());
        Assert.Equal(1, await m1.Get());

        var m2 = f.CreateMemoizR("m2", async () => await v1.Get() * 2);
        Assert.Equal(2, await m2.Get());
        Assert.Equal(2, await m2.Get());

        var m3 = f.CreateMemoizR("m3", async () => await m1.Get() + await m2.Get());

        await v1.Set(2);

        Assert.Equal(6, await m3.Get());
        Assert.Equal(6, await m3.Get());
        Assert.Equal(2, await v1.Get());

        await v1.Set(3);

        Assert.Equal(9, await m3.Get());
        Assert.Equal(9, await m3.Get());
        Assert.Equal(3, await v1.Get());
    }

    [Fact]
    public async Task TestDiamondInvocations()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal("v1", 1);
        var invocationsM1 = 0;
        var m1 = f.CreateMemoizR("m1", async () =>
        {
            invocationsM1++;
            return await v1.Get();
        });
        var invocationsM2 = 0;
        var m2 = f.CreateMemoizR("m2", async () =>
        {
            invocationsM2++;
            return await v1.Get() * 2;
        });
        var invocationsM3 = 0;
        var m3 = f.CreateMemoizR("m3", async () =>
        {
            invocationsM3++;
            return await m1.Get() + await m2.Get();
        });

        await v1.Set(2);

        Assert.Equal(0, invocationsM1);
        Assert.Equal(0, invocationsM2);
        Assert.Equal(0, invocationsM3);
        var r1 = await m3.Get();
        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        Assert.Equal(r1, await m3.Get());

        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        await v1.Set(3);
        var r2 = await m3.Get();
        Assert.Equal(2, invocationsM1);
        Assert.Equal(2, invocationsM2);
        Assert.Equal(2, invocationsM3);

        Assert.Equal(r2, await m3.Get());

        Assert.Equal(2, invocationsM1);
        Assert.Equal(2, invocationsM2);
        Assert.Equal(2, invocationsM3);
    }

    [Fact]
    public async Task TestTwoSourcesInvocations()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal("v1", 1);
        var v2 = f.CreateSignal("v2", 1);
        var invocationsM1 = 0;
        var m1 = f.CreateMemoizR("m1", async () =>
        {
            invocationsM1++;
            return await v1.Get();
        });
        var invocationsM2 = 0;
        var m2 = f.CreateMemoizR("m2", async () =>
        {
            invocationsM2++;
            return await v2.Get() * 2;
        });
        var invocationsM3 = 0;
        var m3 = f.CreateMemoizR("m3", async () =>
        {
            invocationsM3++;
            return await m1.Get() + await m2.Get();
        });

        Assert.Equal(0, invocationsM1);
        Assert.Equal(0, invocationsM2);
        Assert.Equal(0, invocationsM3);

        var r1 = await m3.Get();

        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        Assert.Equal(r1, await m3.Get());

        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        await v1.Set(3);

        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        var r2 = await m3.Get();

        Assert.Equal(2, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(2, invocationsM3);

        Assert.Equal(r2, await m3.Get());

        Assert.Equal(2, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(2, invocationsM3);
    }

    [Fact]
    public async Task TestConcurrency()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);

        await v1.Set(2);

        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var t1 = Task.Run(async () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                await v1.Set(i);
            }
        });

        var t2 = Task.Run(async () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                await v1.Set(i);
            }
        });

        await Task.WhenAll(t1, t2);

        var r1 = await m1.Get();
        Assert.Equal(1998, r1);
    }

    [Fact]
    public async Task TestRelativeConcurrency()
    {
        var f = new MemoFactory();
        var v1 = f.CreateEagerRelativeSignal(1);

        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var t1 = Task.Run(async () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                await v1.Set(i => i + 1);
            }
        });

        var t2 = Task.Run(async () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                await v1.Set(i => i + 1);
            }
        });

        await Task.WhenAll(t1, t2);

        var r1 = await m1.Get();
        Assert.Equal(4002, r1);
    }

    [Fact]
    public async Task TestDependencyUpdate()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(1);
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        Assert.Equal(2, await m1.Get());

        await v1.Set(3);

        Assert.Equal(6, await m1.Get());
    }

    [Fact]
    public async Task TestCaching()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(1);

        var invocationCount = 0;
        var m1 = f.CreateMemoizR(async () =>
        {
            invocationCount++;
            return await v1.Get() * 2;
        });

        // Call 'm1' multiple times to ensure memoization
        await m1.Get();
        await m1.Get();
        await m1.Get();

        // Check if 'm1' was evaluated only once
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task TestThreadSafety()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(1);

        var invocationCount = 0;
        var m1 = f.CreateMemoizR(async () =>
        {
            invocationCount++;
            return await v1.Get() * 2;
        });

        var tasks = new List<Task<int>>();

        await v1.Set(2);

        // Create multiple threads to access 'm1' concurrently
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(m1.Get));
        }

        await Task.WhenAll(tasks);

        // Check if 'm1' was evaluated only once (thread-safe)
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task TestRelativeThreadSafety()
    {
        var f = new MemoFactory();

        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateEagerRelativeSignal(1);

        var invocationCount = 0;
        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(async () =>
        {
            invocationCount++;
            return await v1.Get() * 2;
        });

        // Create multiple threads to set 'v1' concurrently
        var tasks = new List<Task>();
        for (var i = 0; i < 10000; i++)
        {
            tasks.Add(Task.Run(async () => await v1.Set(x => x + 2)));
        }

        await Task.WhenAll(tasks);

        // Check if 'm1' was not evaluated
        Assert.Equal(0, invocationCount);

        Assert.Equal(40002, await m1.Get());

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task TestSignalEquality()
    {
        var f = new MemoFactory();

        // Create two signals with the same initial value
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(1);

        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);
        var m2 = f.CreateMemoizR(async () => await v2.Get() * 2);

        // Check if 'm1' and 'm2' are equal due to the same input signals
        Assert.Equal(await m1.Get(), await m2.Get());

        // Update 'v1' and 'v2' to different values
        await v1.Set(2);
        await v2.Set(3);

        // Confirm that 'm1' and 'm2' not remain equal because of different signal values
        Assert.NotEqual(await m1.Get(), await m2.Get());
    }


    [Fact]
    public async Task TestDynamicSignals()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(1);
        var v3 = f.CreateSignal(true);

        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);
        var m2 = f.CreateMemoizR(async () =>
        {
            if (await v3.Get())
            {
                await v1.Get();
            }
            return await v2.Get() * 2;
        });

        Assert.Empty(v1.Sources);
        Assert.Empty(v1.Observers);

        Assert.Empty(v2.Sources);
        Assert.Empty(v2.Observers);

        Assert.Empty(v3.Sources);
        Assert.Empty(v3.Observers);

        Assert.Empty(m1.Sources);
        Assert.Empty(m1.Observers);

        Assert.Empty(m2.Sources);
        Assert.Empty(m2.Observers);

        await m1.Get();
        await m2.Get();

        Assert.Empty(v1.Sources);
        Assert.Equal(2, v1.Observers.Length);

        Assert.Empty(v2.Sources);
        Assert.Single(v2.Observers);

        Assert.Empty(v3.Sources);
        Assert.Single(v3.Observers);

        Assert.Single(m1.Sources);
        Assert.Empty(m1.Observers);

        Assert.Equal(3, m2.Sources.Length);
        Assert.Empty(m2.Observers);

        await v3.Set(false);

        Assert.Empty(v1.Sources);
        Assert.Equal(2, v1.Observers.Length);

        Assert.Empty(v2.Sources);
        Assert.Single(v2.Observers);

        Assert.Empty(v3.Sources);
        Assert.Single(v3.Observers);

        Assert.Single(m1.Sources);
        Assert.Empty(m1.Observers);

        Assert.Equal(3, m2.Sources.Length);
        Assert.Empty(m2.Observers);

        await m1.Get();
        await m2.Get();

        Assert.Empty(v1.Sources);
        Assert.Single(v1.Observers);

        Assert.Empty(v2.Sources);
        Assert.Single(v2.Observers);

        Assert.Empty(v3.Sources);
        Assert.Single(v3.Observers);

        Assert.Single(m1.Sources);
        Assert.Empty(m1.Observers);

        Assert.Equal(2, m2.Sources.Length);
        Assert.Empty(m2.Observers);
    }
}