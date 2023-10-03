namespace MemoizR.Test;

public class Core
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
        var v1 = f.CreateSignal(1, "v1");
        Assert.Equal(1, await v1.Get());

        var m1 = f.CreateMemoizR(async () => await v1.Get(), "m1");
        Assert.Equal(1, await m1.Get());
        Assert.Equal(1, await m1.Get());

        var m2 = f.CreateMemoizR(async () => await v1.Get() * 2, "m2");
        Assert.Equal(2, await m2.Get());
        Assert.Equal(2, await m2.Get());

        var m3 = f.CreateMemoizR(async () => await m1.Get() + await m2.Get(), "m3");

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
        var v1 = f.CreateSignal(1, "v1");
        var invocationsM1 = 0;
        var m1 = f.CreateMemoizR(async () =>
        {
            invocationsM1++;
            return await v1.Get();
        }, "m1");
        var invocationsM2 = 0;
        var m2 = f.CreateMemoizR(async () =>
        {
            invocationsM2++;
            return await v1.Get() * 2;
        }, "m2");
        var invocationsM3 = 0;
        var m3 = f.CreateMemoizR(async () =>
        {
            invocationsM3++;
            return await m1.Get() + await m2.Get();
        }, "m3");

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
        var v1 = f.CreateSignal(1, "v1");
        var v2 = f.CreateSignal(1, "v2");
        var invocationsM1 = 0;
        var m1 = f.CreateMemoizR(async () =>
        {
            invocationsM1++;
            return await v1.Get();
        }, "m1");
        var invocationsM2 = 0;
        var m2 = f.CreateMemoizR(async () =>
        {
            invocationsM2++;
            return await v2.Get() * 2;
        }, "m2");
        var invocationsM3 = 0;
        var m3 = f.CreateMemoizR(async () =>
        {
            invocationsM3++;
            return await m1.Get() + await m2.Get();
        }, "m3");

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
        // Create a MemoFactory instance
        var f = new MemoFactory();

        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(1);

        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        // Check the initial value of 'm1'
        Assert.Equal(2, await m1.Get());

        // Update 'v1' to 3
        await v1.Set(3);

        // Confirm that 'm1' updates automatically due to the change in 'v1'
        Assert.Equal(6, await m1.Get());
    }

    [Fact]
    public async Task TestCaching()
    {
        // Create a MemoFactory instance
        var f = new MemoFactory();


        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(1);

        var invocationCount = 0;
        // Create a memoized computation 'm1' that depends on 'v1'
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
        // Create a MemoFactory instance
        var f = new MemoFactory();

        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(1);

        var invocationCount = 0;
        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(async () =>
        {
            invocationCount++;
            return await v1.Get() * 2;
        });

        // Create multiple threads to access 'm1' concurrently
        var tasks = new List<Task<int>>();

        await v1.Set(2);
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () => await m1.Get()));
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        // Check if 'm1' was evaluated only once (thread-safe)
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task TestRelativeThreadSafety()
    {
        // Create a MemoFactory instance
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

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        // Check if 'm1' was not evaluated
        Assert.Equal(0, invocationCount);

        Assert.Equal(40002, await m1.Get());

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task TestSignalEquality()
    {
        // Create a MemoFactory instance
        var f = new MemoFactory();

        // Create two signals with the same initial value
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(1);

        // Create a memoized computation 'm1' for each signal
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);
        var m2 = f.CreateMemoizR(async () => await v2.Get() * 2);

        // Check if 'm1' and 'm2' are equal due to the same input signals
        Assert.Equal(await m1.Get(), await m2.Get());

        // Update 'v1' and 'v2' to different values
        await v1.Set(2);
        await v2.Set(3);

        // Confirm that 'm1' and 'm2' remain equal despite different signal values
        Assert.NotEqual(await m1.Get(), await m2.Get());
    }
}