using MemoizR.Reactive;

namespace MemoizR.Test;

public class Reactive
{
    [Fact]
    public void TestReactive()
    {
        var f = new ReactiveMemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, v1.Get());
        Assert.Equal(1, v1.Get());

        var m1 = f.CreateReaction(() => v1.Get());

        v1.Set(2);
    }

    [Fact]
    public void TestReactiveInvocations()
    {
        var invocations = 0;
        var f = new ReactiveMemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, v1.Get());

        var m1 = f.CreateReaction(() =>
        {
            invocations++;
            return v1.Get();
        });

        Assert.Equal(1, invocations);

        v1.Set(2);

        Assert.Equal(2, invocations);

        v1.Set(2);

        Assert.Equal(2, invocations);
    }


    [Fact]
    public void TestReactionReducR()
    {
        var invocations = 0;
        var f = new ReactiveMemoFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, v1.Get());

        var m1 = f.CreateReactionReducR<int>((i) =>
        {
            invocations++;
            var vr1 = v1.Get();
            return vr1 + i;
        });

        Assert.Equal(1, invocations);

        v1.Set(2);

        Assert.Equal(2, invocations);

        Assert.Equal(3, m1.Get());

        v1.Set(2);

        Assert.Equal(3, invocations);

        Assert.Equal(5, m1.Get());
    }

    [Fact]
    public async Task TestThreadSafety()
    {
        // Create a MemoFactory instance
        var f = new ReactiveMemoFactory();

        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(4);

        var invocationCount = 0;
        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(() => v1.Get() * 2);

        var r1 = f.CreateReaction(() =>
        {
            invocationCount++;
            return m1.Get();
        });

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() => v1.Set(i)));
        }

        await Task.Delay(1); // wait for m1.Get to be able to read

        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() => v1.Set(i)));
            tasks.Add(Task.Run(() => v1.Set(i)));
        }

        var resultM1 = 0;
        tasks.Add(Task.Run(() => resultM1 = m1.Get()));

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        Assert.Equal(40, resultM1);
        Assert.Equal(40, m1.Get());

        // Check if 'r1' was evaluated three times (thread-safe)
        // This is not completely reliable because if all the set are evaluated the gets trigger again because how the readwrite lock works
        Assert.InRange(invocationCount, 3, 30);
    }

    [Fact]
    public async Task TestThreadSafety2()
    {
        // Create a MemoFactory instance
        var f = new ReactiveMemoFactory();

        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(4);

        var invocationCount = 0;
        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(() => v1.Get() * 2);

        var r1 = f.CreateReaction(() =>
        {
            invocationCount++;
            return m1.Get();
        });

        var tasks = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() => v1.Set(i)));
            await Task.Delay(1);
            tasks.Add(Task.Run(() => v1.Set(i)));
        }

        await Task.Delay(1);
        var resultM1 = 0;
        tasks.Add(Task.Run(() => resultM1 = m1.Get()));

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        Assert.Equal(40, resultM1);
        Assert.Equal(40, m1.Get());

        // Check if 'r1' was evaluated 22 times (thread-safe)
        // This is not completely reliable because if all the set are evaluated the gets trigger again because how the readwrite lock works
        Assert.InRange(invocationCount, 22, 30);
    }
}