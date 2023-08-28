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

    [Fact]
    public void TestComputedInvocations()
    {
        var invocations = 0;
        var v1 = new MemoSetR<int>(1);
        Assert.Equal(1, v1.Get());

        var m1 = new MemoizR<int>(() =>
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
        var v1 = new MemoSetR<int>(1, "v1");
        Assert.Equal(1, v1.Get());

        var m1 = new MemoizR<int>(() => v1.Get(), "m1");
        Assert.Equal(1, m1.Get());
        Assert.Equal(1, m1.Get());

        var m2 = new MemoizR<int>(() => v1.Get() * 2, "m2");
        Assert.Equal(2, m2.Get());
        Assert.Equal(2, m2.Get());

        var m3 = new MemoizR<int>(() => m1.Get() + m2.Get(), "m3");

        v1.Set(2);

        Assert.Equal(6, m3.Get());
        Assert.Equal(6, m3.Get());
        Assert.Equal(2, v1.Get());
    }

    [Fact]
    public void TestDiamondInvocations()
    {
        var v1 = new MemoSetR<int>(1, "v1");
        var invocationsM1 = 0;
        var m1 = new MemoizR<int>(() =>
        {
            invocationsM1++;
            return v1.Get();
        }, "m1");
        var invocationsM2 = 0;
        var m2 = new MemoizR<int>(() =>
        {
            invocationsM2++;
            return v1.Get() * 2;
        }, "m2");
        var invocationsM3 = 0;
        var m3 = new MemoizR<int>(() =>
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
        m3.Get();
        Assert.Equal(1, invocationsM1);
        Assert.Equal(1, invocationsM2);
        Assert.Equal(1, invocationsM3);

        v1.Set(3);
        var r2 = m3.Get();
        Assert.Equal(2, invocationsM1);
        Assert.Equal(2, invocationsM2);
        Assert.Equal(1, invocationsM3);
        m3.Get();
        Assert.Equal(2, invocationsM1);
        Assert.Equal(2, invocationsM2);
        Assert.Equal(1, invocationsM3);
    }
}