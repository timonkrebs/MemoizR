using MemoizR;
namespace MemoizR.Test;

public class Core
{
    [Fact]
    public void TestInitialization()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, v1.Get());
        Assert.Equal(1, v1.Get());

        v1.Set(2);
        Assert.Equal(2, v1.Get());
        Assert.Equal(2, v1.Get());

        var m1 = f.CreateMemoizR(() => 3);

        Assert.Equal(3, m1.Get());
        Assert.Equal(3, m1.Get());
    }

    [Fact]
    public void TestComputed()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, v1.Get());

        var m1 = f.CreateMemoizR(() => v1.Get());
        Assert.Equal(1, m1.Get());
        Assert.Equal(1, m1.Get());

        v1.Set(2);

        Assert.Equal(2, m1.Get());
        Assert.Equal(2, m1.Get());
        Assert.Equal(2, v1.Get());

        v1.Set(4);

        Assert.Equal(4, m1.Get());
        Assert.Equal(4, m1.Get());
        Assert.Equal(4, v1.Get());
    }

    [Fact]
    public void TestComputedInvocations()
    {
        var invocations = 0;
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, v1.Get());

        var m1 = f.CreateMemoizR(() =>
        {
            invocations++;
            return v1.Get();
        });

        v1.Set(2);

        Assert.Equal(0, invocations);
        m1.Get();
        Assert.Equal(1, invocations);
        m1.Get();
        Assert.Equal(1, invocations);
        m1.Get();
        Assert.Equal(1, invocations);
        m1.Get();
    }

    [Fact]
    public void TestDiamond()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1, "v1");
        Assert.Equal(1, v1.Get());

        var m1 = f.CreateMemoizR(() => v1.Get(), "m1");
        Assert.Equal(1, m1.Get());
        Assert.Equal(1, m1.Get());

        var m2 = f.CreateMemoizR(() => v1.Get() * 2, "m2");
        Assert.Equal(2, m2.Get());
        Assert.Equal(2, m2.Get());

        var m3 = f.CreateMemoizR(() => m1.Get() + m2.Get(), "m3");

        v1.Set(2);

        Assert.Equal(6, m3.Get());
        Assert.Equal(6, m3.Get());
        Assert.Equal(2, v1.Get());

        v1.Set(3);

        Assert.Equal(9, m3.Get());
        Assert.Equal(9, m3.Get());
        Assert.Equal(3, v1.Get());
    }

    [Fact]
    public void TestDiamondInvocations()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1, "v1");
        var invocationsM1 = 0;
        var m1 = f.CreateMemoizR(() =>
        {
            invocationsM1++;
            return v1.Get();
        }, "m1");
        var invocationsM2 = 0;
        var m2 = f.CreateMemoizR(() =>
        {
            invocationsM2++;
            return v1.Get() * 2;
        }, "m2");
        var invocationsM3 = 0;
        var m3 = f.CreateMemoizR(() =>
        {
            invocationsM3++;
            return m1.Get() + m2.Get();
        }, "m3");

        v1.Set(2);

        Assert.Equal(0, invocationsM1);
        Assert.Equal(0, invocationsM2);
        Assert.Equal(0, invocationsM3);
        var r1 = m3.Get();
        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        Assert.Equal(r1, m3.Get());

        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        v1.Set(3);
        var r2 = m3.Get();
        Assert.Equal(2, invocationsM1);
        Assert.Equal(2, invocationsM2);
        Assert.Equal(2, invocationsM3);

        Assert.Equal(r2, m3.Get());

        Assert.Equal(2, invocationsM1);
        Assert.Equal(2, invocationsM2);
        Assert.Equal(2, invocationsM3);
    }

    [Fact]
    public void TestTwoSourcesInvocations()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1, "v1");
        var v2 = f.CreateSignal(1, "v2");
        var invocationsM1 = 0;
        var m1 = f.CreateMemoizR(() =>
        {
            invocationsM1++;
            return v1.Get();
        }, "m1");
        var invocationsM2 = 0;
        var m2 = f.CreateMemoizR(() =>
        {
            invocationsM2++;
            return v2.Get() * 2;
        }, "m2");
        var invocationsM3 = 0;
        var m3 = f.CreateMemoizR(() =>
        {
            invocationsM3++;
            return m1.Get() + m2.Get();
        }, "m3");

        Assert.Equal(0, invocationsM1);
        Assert.Equal(0, invocationsM2);
        Assert.Equal(0, invocationsM3);

        var r1 = m3.Get();

        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        Assert.Equal(r1, m3.Get());

        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        v1.Set(3);
        var r2 = m3.Get();

        Assert.Equal(2, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(2, invocationsM3);

        Assert.Equal(r2, m3.Get());

        Assert.Equal(2, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(2, invocationsM3);
    }

    [Fact]
    public async Task TestConcurrency()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);

        v1.Set(2);

        var m1 = f.CreateMemoizR(() => v1.Get() * 2);

        var t1 = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                v1.Set(i);
            }
        });

        var t2 = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                v1.Set(i);
            }
        });

        await Task.WhenAll(t1, t2);

        var r1 = m1.Get();
        Assert.Equal(1998, r1);
    }

    [Fact]
    public async Task TestRelativeConcurrency()
    {
        var f = new MemoFactory();
        var v1 = f.CreateEagerRelativeSignal(1);

        var m1 = f.CreateMemoizR(() => v1.Get() * 2);

        var t1 = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                v1.Set(i => i + 1);
            }
        });

        var t2 = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                v1.Set(i => i + 1);
            }
        });

        await Task.WhenAll(t1, t2);

        var r1 = m1.Get();
        Assert.Equal(4002, r1);
    }

    [Fact]
    public void TestDependencyUpdate()
    {
        // Create a MemoFactory instance
        var f = new MemoFactory();

        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(1);

        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(() => v1.Get() * 2);

        // Check the initial value of 'm1'
        Assert.Equal(2, m1.Get());

        // Update 'v1' to 3
        v1.Set(3);

        // Confirm that 'm1' updates automatically due to the change in 'v1'
        Assert.Equal(6, m1.Get());
    }

    [Fact]
    public void TestCaching()
    {
        // Create a MemoFactory instance
        var f = new MemoFactory();


        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(1);

        var invocationCount = 0;
        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(() =>
        {
            invocationCount++;
            return v1.Get() * 2;
        });

        // Call 'm1' multiple times to ensure memoization
        m1.Get();
        m1.Get();
        m1.Get();

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
        var m1 = f.CreateMemoizR(() =>
        {
            invocationCount++;
            return v1.Get() * 2;
        });

        // Create multiple threads to access 'm1' concurrently
        var tasks = new List<Task<int>>();

        v1.Set(2);
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() => m1.Get()));
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
        var m1 = f.CreateMemoizR(() =>
        {
            invocationCount++;
            return v1.Get() * 2;
        });

        // Create multiple threads to set 'v1' concurrently
        var tasks = new List<Task>();
        for (var i = 0; i < 10000; i++)
        {
            tasks.Add(Task.Run(() => v1.Set(x => x + 2)));
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        // Check if 'm1' was not evaluated
        Assert.Equal(0, invocationCount);

        Assert.Equal(40002, m1.Get());

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void TestSignalEquality()
    {
        // Create a MemoFactory instance
        var f = new MemoFactory();

        // Create two signals with the same initial value
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(1);

        // Create a memoized computation 'm1' for each signal
        var m1 = f.CreateMemoizR(() => v1.Get() * 2);
        var m2 = f.CreateMemoizR(() => v2.Get() * 2);

        // Check if 'm1' and 'm2' are equal due to the same input signals
        Assert.Equal(m1.Get(), m2.Get());

        // Update 'v1' and 'v2' to different values
        v1.Set(2);
        v2.Set(3);

        // Confirm that 'm1' and 'm2' remain equal despite different signal values
        Assert.NotEqual(m1.Get(), m2.Get());
    }
}