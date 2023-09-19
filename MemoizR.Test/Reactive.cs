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
}