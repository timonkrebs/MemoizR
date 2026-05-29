namespace MemoizR.Tests;

public class UntrackTests
{
    [Fact]
    public async Task TestUntrack()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);
        var invocationCount = 0;
        var m1 = f.CreateMemoizR(async () =>
        {
            invocationCount++;
            return await f.Untrack(async () => await v1.Get()) * 2;
        });

        Assert.Equal(2, await m1.Get());
        Assert.Equal(1, invocationCount);

        await v1.Set(2);

        // m1 should not be dirty because v1 was untracked, so it should return the cached value.
        Assert.Equal(2, await m1.Get());
        Assert.Equal(1, invocationCount);

        // Now v2 is tracked normally
        var v2 = f.CreateSignal(10);
        var m2 = f.CreateMemoizR(async () =>
        {
            return await m1.Get() + await v2.Get();
        });

        Assert.Equal(12, await m2.Get());

        await v1.Set(3);
        // m1 still not dirty
        Assert.Equal(12, await m2.Get());

        await v2.Set(20);
        // m2 is dirty because of v2
        Assert.Equal(22, await m2.Get());
    }
}
