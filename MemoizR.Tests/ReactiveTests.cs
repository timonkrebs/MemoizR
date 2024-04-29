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

    [Fact(Skip="Experimantal")]
    public async Task TestAsyncEnumerable()
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
            }
        });

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
    }

    [Fact(Skip="Experimantal")]
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
        await Task.Delay(200);
        Assert.Equal(2, result);
        Assert.Equal(1, invocationCount);

        await v1.Set(3);
        await Task.Delay(200);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);

        await v1.Set(3);
        await Task.Delay(200);
        Assert.Equal(3, result);
        Assert.Equal(2, invocationCount);

        await v1.Set(4);
        await Task.Delay(200);
        Assert.Equal(4, result);
        Assert.Equal(3, invocationCount);
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

    [Fact(Timeout = 1000)]
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

        _ = v1.Set(1000);
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

        Assert.Equal(1, invocationCount);
    }

    [Fact(Timeout = 1000)]
    public async Task TestThreadSafety2()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var invocationCount = 0;
        var r1 = f.BuildReaction().CreateReaction(m1, m => invocationCount++);

        var tasks = new List<Task>();
        for (var i = 0; i < 200; i++)
        {
            tasks.Add(Task.Run(async () => await v1.Set(i)));
            tasks.Add(Task.Run(async () => await v1.Set(i)));
        }

        await Task.Delay(1);
        var resultM1 = 0;
        tasks.Add(Task.Run(async () => resultM1 = await m1.Get()));

        await Task.WhenAll(tasks);
        await Task.Delay(100);

        Assert.Equal(400, await m1.Get());
        Assert.Equal(400, resultM1);

        await Task.Delay(100);

        Assert.Equal(1, invocationCount);
    }

    [Fact(Timeout = 2000)]
    public async Task TestThreadSafety3()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);

        var memoInvocationCount = 0;
        var m1 = f.CreateMemoizR(async () =>
        {
            memoInvocationCount++;
            return await v1.Get() * 2;
        });

        var invocationCountR1 = 0;
        var r1 = f.BuildReaction().CreateReaction(m1, m => invocationCountR1++);
        var invocationCountR2 = 0;
        var r2 = f.BuildReaction().CreateReaction(m1, m => invocationCountR2++);

        await Task.Delay(100);

        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            tasks.Add(Task.Run(async () => await v1.Set(i)));
            await Task.Delay(35);
        }

        tasks.Add(Task.Run(async () => await v1.Set(41)));

        await Task.Delay(1);
        var resultM1 = 0;
        tasks.Add(Task.Run(async () => resultM1 = await m1.Get()));

        await Task.WhenAll(tasks);
        await Task.Delay(100);

        Assert.Equal(82, await m1.Get());
        Assert.Equal(82, resultM1);
    }

    [Fact(Timeout = 2000)]
    public async Task TestThreadSafety4()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);

        var memoInvocationCount = 0;
        var m1 = f.CreateMemoizR(async () =>
        {
            memoInvocationCount++;
            return await v1.Get() * 2;
        });

        var invocationCountR1 = 0;
        var r1 = f.BuildReaction().CreateReaction(m1, m => invocationCountR1++);
        var invocationCountR2 = 0;
        var r2 = f.BuildReaction().CreateReaction(m1, m => invocationCountR2++);

        await Task.Delay(100);

        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            tasks.Add(Task.Run(async () => await v1.Set(i)));
            await Task.Delay(35);
        }

        tasks.Add(Task.Run(async () => await v1.Set(41)));

        await Task.WhenAll(tasks);

        Assert.Equal(82, await m1.Get());

        await Task.Delay(100);

        Assert.Equal(42, invocationCountR1);
        Assert.Equal(42, invocationCountR2);
        Assert.Equal(42, memoInvocationCount);
    }
}