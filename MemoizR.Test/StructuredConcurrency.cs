using MemoizR.Reactive;
using MemoizR.StructuredConcurrency;

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
}