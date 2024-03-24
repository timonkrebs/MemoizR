namespace MemoizR.Test;

public class Reactive
{
    [Fact]
    public async Task TestReactive()
    {
        var f = new MemoFactory("reactivity");
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());
        Assert.Equal(1, await v1.Get());

        var m1 = f.BuildReaction()
        .CreateReaction(v1, v => { });

        await v1.Set(2);
    }

    [Fact]
    public async Task TestReactiveInvocations()
    {
        var invocations = 0;
        var f = new MemoFactory("reactivity");
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());

        var m1 = f.BuildReaction()
        .CreateReaction(v1, v => invocations++);

        Assert.Equal(1, invocations);
        await Task.Delay(100);

        await v1.Set(2);
        await Task.Delay(100);

        Assert.Equal(2, invocations);

        await v1.Set(2);
        await Task.Delay(20);

        Assert.Equal(2, invocations);
    }

    [Fact]
    public async Task TestThreadSafety()
    {
        // Create a MemoFactory instance
        var f = new MemoFactory("reactivity");

        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(4);

        var invocationCount = 0;
        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);


        var result = 0;
        var r1 = f.BuildReaction()
        .CreateReaction(m1, m =>
        {
            invocationCount++;
            result = m;
        });

        var _ = v1.Set(1000);
        for (var i = 0; i < 100; i++)
        {
            _ = v1.Set(i);
        }

        for (var i = 0; i < 21; i++)
        {
            _ = v1.Set(i);
            _ = v1.Set(i);
        }

        var resultM1 = 0;
        _ = Task.Run(async () => resultM1 = await m1.Get());

        await Task.Delay(100);

        Assert.Equal(40, await m1.Get());
        Assert.Equal(40, resultM1);
        Assert.Equal(40, result);
        Assert.Equal(await m1.Get(), result);

        await Task.Delay(100);

        // This is not completely reliable because if all the set are evaluated await he gets trigger again because how the readwrite lock works
        Assert.InRange(invocationCount, 2, 60);
    }

    [Fact]
    public async Task TestThreadSafety2()
    {
        // Create a MemoFactory instance
        var f = new MemoFactory("reactivity");

        // Create a signal 'v1' with an initial value of 1
        var v1 = f.CreateSignal(4);

        var invocationCount = 0;
        // Create a memoized computation 'm1' that depends on 'v1'
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var r1 = f.BuildReaction().CreateReaction(m1, m => invocationCount++);


        var tasks = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(async () => await v1.Set(i)));
            await Task.Delay(1);
            tasks.Add(Task.Run(async () => await v1.Set(i)));
        }

        await Task.Delay(1);
        var resultM1 = 0;
        tasks.Add(Task.Run(async () => resultM1 = await m1.Get()));

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);
        await Task.Delay(100);

        Assert.Equal(40, await m1.Get());
        Assert.Equal(40, resultM1);

        await Task.Delay(100);

        // This is not completely reliable because if all the set are evaluated await he gets trigger again because how the readwrite lock works
        Assert.InRange(invocationCount, 2, 25);
    }
}