using MemoizR;

namespace MemoizR.Test;

public class UnitTest1
{
    [Fact]
    public void TestInitialization()
    {
        var v1 = new MemoSetR<int>(1);
        Assert.Equal(1, v1.Get());
        Assert.Equal(1, v1.Get());

        v1.Set(2);
        Assert.Equal(2, v1.Get());
        Assert.Equal(2, v1.Get());

        var m1 = new MemoizR<int>(() => 3);

        Assert.Equal(3, m1.Get());
        Assert.Equal(3, m1.Get());
    }

    [Fact]
    public void TestComputed()
    {
        var v1 = new MemoSetR<int>(1);
        Assert.Equal(1, v1.Get());

        var m1 = new MemoizR<int>(() => v1.Get());
        Assert.Equal(1, m1.Get());
        Assert.Equal(1, m1.Get());

        v1.Set(2);

        Assert.Equal(2, m1.Get());
        Assert.Equal(2, m1.Get());
        Assert.Equal(2, v1.Get());
    }
}