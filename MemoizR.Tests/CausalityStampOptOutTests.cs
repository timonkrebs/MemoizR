namespace MemoizR.Tests;

// The stamp opt-out (MemoFactoryOptions.DisableCausalityStamps): single-process graphs that
// never exchange stamps skip the capture machinery entirely. The contract under test: reactivity
// is untouched, and every publication reads as UNVERIFIABLE (the empty stamp plus the flag) --
// never as the honest-empty None a consistency check could trust.
public class CausalityStampOptOutTests
{
    [Fact]
    public async Task DisabledContext_PublishesUnverifiableEmptyEvidence()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.DisableCausalityStamps);
        var s = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await s.Get() * 10);

        var (signalValue, signalStamp) = await s.GetWithStamp();
        Assert.Equal(1, signalValue);
        Assert.Equal(CausalityStamp.Empty, signalStamp);
        Assert.True(s.Evidence.Unverifiable);

        var (memoValue, memoEvidence) = await m.GetWithEvidence();
        Assert.Equal(10, memoValue);
        Assert.Equal(CausalityStamp.Empty, memoEvidence.Stamp);
        Assert.True(memoEvidence.Unverifiable);
        Assert.Empty(m.SourceStamps);

        // A Set must not start stamping either: the write path never constructs a stamp.
        await s.Set(2);
        var (_, afterSetStamp) = await s.GetWithStamp();
        Assert.Equal(CausalityStamp.Empty, afterSetStamp);
        Assert.True(s.Evidence.Unverifiable);
    }

    [Fact]
    public async Task DisabledContext_ReactivityIsUntouched()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.DisableCausalityStamps);
        var s = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await s.Get() + 1);

        Assert.Equal(2, await m.Get());

        await s.Set(41);
        Assert.Equal(42, await m.Get());

        // The equal-value write shortcut still applies.
        await s.Set(41);
        Assert.Equal(42, await m.Get());
    }

    [Fact]
    public async Task DisabledContext_EagerRelativeSignal_WorksUnstamped()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.DisableCausalityStamps);
        var r = f.CreateEagerRelativeSignal(1);

        await r.Set(v => v + 1);
        Assert.Equal(2, await r.Get());

        var (_, stamp) = await r.GetWithStamp();
        Assert.Equal(CausalityStamp.Empty, stamp);
        Assert.True(r.Evidence.Unverifiable);
    }

    [Fact]
    public void KeyedContext_ConflictingStampSetting_Throws()
    {
        var key = $"stamp-optout-{Guid.NewGuid()}";
        var enabled = new MemoFactory(key);

        var exception = Assert.Throws<ArgumentException>(
            () => new MemoFactory(key, options: MemoFactoryOptions.DisableCausalityStamps));
        Assert.Contains("causality stamps", exception.Message);

        // Same setting shares the context, as before.
        var second = new MemoFactory(key);
        Assert.Same(enabled.Context, second.Context);
        GC.KeepAlive(enabled);
    }

    [Fact]
    public async Task DefaultContext_KeepsStampingUnchanged()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        await s.Set(2);

        var (_, stamp) = await s.GetWithStamp();
        Assert.True(stamp.TryGetTrigger(s.Id, out var trigger));
        Assert.Equal(1, trigger);
        Assert.False(s.Evidence.Unverifiable);
    }
}
