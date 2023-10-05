using MemoizR.Reactive;
using MemoizR.StructuredConcurrency;
using Xunit.Sdk;

namespace MemoizR.Test;

public class StructuredConcurrency
{
    [Fact(Skip = "Blocks Testsuite")]
    public async Task TestInitialization()
    {
        var f = new MemoFactory();
        var fsc = new StructuredConcurrencyFactory();
        var v1 = f.CreateSignal(1);
        Assert.Equal(1, await v1.Get());
        Assert.Equal(1, await v1.Get());

        await v1.Set(2);
        Assert.Equal(2, await v1.Get());
        Assert.Equal(2, await v1.Get());

        await v1.Set(3);

        var c1 = fsc.CreateConcurrentMapReduce(
            (val, agg) => agg - val,
            _ => v1.Get(),
            _ => Task.FromResult(3));

        Assert.Equal(-6, await c1.Get());
        Assert.Equal(-6, await c1.Get());

        var c2 = fsc.CreateConcurrentMapReduce(
            _ => v1.Get(),
            _ => c1.Get());

        Assert.Equal(-3, await c2.Get());
    }

    [Fact(Timeout = 1000)]
    public void TestExceptionHandling()
    {
        var f = new MemoFactory();
        var fsc = new StructuredConcurrencyFactory();

        // all tasks get canceled if one fails
        var c1 = fsc.CreateConcurrentMapReduce(
            _ => throw new Exception(),
            async c =>
            {
                await Task.Delay(3000, c);
                return 4;
            },
            _ => throw new SkipException("Test"));

        var e = Assert.Throws<AggregateException>(() => c1.Get().Result);
        Assert.Equal(1, e.InnerExceptions.Count);
    }
}