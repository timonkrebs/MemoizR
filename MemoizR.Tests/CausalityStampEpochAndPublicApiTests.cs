using System.Reflection;
using MemoizR.StructuredConcurrency;

namespace MemoizR.Tests;

// Phase 3 of the Causality Trigger Clock (issue #39): reset resilience via incarnation epochs,
// and the public read surface (GetWithStamp, Stamp, SourceStamps, Id) a distributed sync layer
// consumes.
public class CausalityStampEpochAndPublicApiTests
{
    [Fact]
    public void Contexts_GetDistinctNonZeroEpochs()
    {
        var f1 = new MemoFactory();
        var f2 = new MemoFactory();
        var s1 = f1.CreateSignal(1);
        var s1b = f1.CreateSignal(2);
        var s2 = f2.CreateSignal(1);

        Assert.NotEqual(0, s1.Stamp.Epoch);
        Assert.Equal(s1.Stamp.Epoch, s1b.Stamp.Epoch);   // one incarnation per context
        Assert.NotEqual(s1.Stamp.Epoch, s2.Stamp.Epoch); // distinct across contexts
        Assert.Equal(0, CausalityStamp.Empty.Epoch);
    }

    [Fact]
    public async Task RestartedGraph_IsNeverConfusedWithItsPreResetIncarnation()
    {
        // The reset trap the epoch exists for: a stamp escapes over the wire, the process
        // restarts, and the recreated graph reissues the SAME id with the SAME trigger for a
        // DIFFERENT value history.
        var f1 = new MemoFactory();
        var sOld = f1.CreateSignal(1);
        await sOld.Set(2); // trigger 1
        var wire = sOld.Stamp.Serialize();

        var f2 = new MemoFactory();
        var sNew = f2.CreateSignal(1);
        await sNew.Set(99); // same construction order -> same id, same trigger 1
        Assert.Equal(sOld.Id, sNew.Id);

        var preReset = CausalityStamp.Deserialize(wire);
        Assert.True(preReset.TryGetTrigger(sNew.Id, out var oldTrigger));
        Assert.True(sNew.Stamp.TryGetTrigger(sNew.Id, out var newTrigger));
        Assert.Equal(oldTrigger, newTrigger); // identical (id, trigger) -- the trap...

        // ...which the epoch defuses: never equal, never consistent, never silently merged.
        Assert.NotEqual(preReset, sNew.Stamp);
        Assert.False(preReset.IsConsistentWith(sNew.Stamp));
        Assert.False(sNew.Stamp.IsConsistentWith(preReset));
        Assert.Throws<InvalidOperationException>(() => preReset.Join(sNew.Stamp));
    }

    [Fact]
    public async Task EmptyStamp_IsEpochAgnostic()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        await s.Set(2);

        Assert.Equal(s.Stamp, CausalityStamp.Empty.Join(s.Stamp));
        Assert.Equal(s.Stamp, s.Stamp.Join(CausalityStamp.Empty));
        Assert.True(CausalityStamp.Empty.IsConsistentWith(s.Stamp));
        Assert.True(s.Stamp.IsConsistentWith(CausalityStamp.Empty));
    }

    [Fact]
    public async Task Serialization_RoundTripsTheGraphEpoch()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await s.Get() * 2);
        await s.Set(3);
        await m.Get();

        var restored = CausalityStamp.Deserialize(m.Stamp.Serialize());
        Assert.Equal(m.Stamp, restored);
        Assert.Equal(m.Stamp.Epoch, restored.Epoch);
        Assert.True(restored.IsConsistentWith(s.Stamp));
    }

    [Fact]
    public async Task GetWithStamp_ReturnsThePairOfOnePublication()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);

        // The writer publishes value i with trigger i in one box swap; readers using the
        // public pair API must never observe a mixed pair.
        var stop = false;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                var (value, stamp) = await s.GetWithStamp();
                Assert.True(stamp.TryGetTrigger(s.Id, out var trigger));
                Assert.Equal(value, trigger);
            }
        })).ToArray();

        for (var i = 1; i <= 1000; i++)
        {
            await s.Set(i);
        }

        Volatile.Write(ref stop, true);
        await Task.WhenAll(readers);

        var (finalValue, finalStamp) = await s.GetWithStamp();
        Assert.Equal(1000, finalValue);
        Assert.Equal(finalStamp, s.Stamp);
    }

    [Fact]
    public async Task GetWithStamp_TracksDependenciesLikeGet()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(2);
        var m = f.CreateMemoizR(async () => (await s.GetWithStamp()).Value * 10);

        Assert.Equal(20, await m.Get());
        Assert.True(m.Stamp.TryGetTrigger(s.Id, out var trigger));
        Assert.Equal(0, trigger);

        // The dependency edge exists: a Set propagates and the memo recomputes.
        await s.Set(3);
        Assert.Equal(30, await m.Get());
        Assert.True(m.Stamp.TryGetTrigger(s.Id, out trigger));
        Assert.Equal(1, trigger);
    }

    [Fact]
    public async Task GetWithStamp_OnADirtyMemo_RecomputesAndPairsFreshStampWithFreshValue()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await s.Get() + 100);

        var (value, stamp) = await m.GetWithStamp();
        Assert.Equal(101, value);
        Assert.True(stamp.TryGetTrigger(s.Id, out var trigger));
        Assert.Equal(0, trigger);

        await s.Set(2); // memo is now dirty; GetWithStamp must pull, not serve the stale pair
        (value, stamp) = await m.GetWithStamp();
        Assert.Equal(102, value);
        Assert.True(stamp.TryGetTrigger(s.Id, out trigger));
        Assert.Equal(1, trigger);
        Assert.Equal(stamp, m.Stamp);
    }

    [Fact]
    public async Task GetWithStamp_OnEagerRelativeSignalAndConcurrentNodes()
    {
        var f = new MemoFactory();

        var eager = f.CreateEagerRelativeSignal(1);
        await eager.Set(v => v + 1);
        var (eagerValue, eagerStamp) = await eager.GetWithStamp();
        Assert.Equal(2, eagerValue);
        Assert.True(eagerStamp.TryGetTrigger(eager.Id, out var eagerTrigger));
        Assert.Equal(1, eagerTrigger);

        var s = f.CreateSignal(5);
        var race = f.CreateConcurrentRace<int, int>(
            async () => await s.Get(),
            async (_, input) => input * 2);
        var (raceValue, raceStamp) = await race.GetWithStamp();
        Assert.Equal(10, raceValue);
        Assert.True(raceStamp.TryGetTrigger(s.Id, out var raceTrigger));
        Assert.Equal(0, raceTrigger);

        var cmr = f.CreateConcurrentMapReduce(
            async _ => await s.Get(),
            async _ => await eager.Get());
        var (cmrValue, cmrStamp) = await cmr.GetWithStamp();
        Assert.Equal(7, cmrValue);
        Assert.True(cmrStamp.TryGetTrigger(s.Id, out _));
        Assert.True(cmrStamp.TryGetTrigger(eager.Id, out _));
    }

    [Fact]
    public void PublicSurface_IsActuallyPublic()
    {
        // The friend assembly makes internals invisible to these tests' compiler errors, so pin
        // the sync-layer surface with reflection: what a third-party assembly can reach.
        Assert.True(typeof(CausalityStamp).IsPublic);
        Assert.True(typeof(IStampedGetR<int>).IsPublic || typeof(IStampedGetR<int>).IsNestedPublic);

        Assert.NotNull(typeof(Signal<int>).GetMethod("GetWithStamp", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(MemoizR<int>).GetMethod("GetWithStamp", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(SignalHandlR).GetProperty("Stamp", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(SignalHandlR).GetProperty("SourceStamps", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(SignalHandlR).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance));

        foreach (var member in new[] { "Epoch", "Triggers" })
        {
            Assert.NotNull(typeof(CausalityStamp).GetProperty(member, BindingFlags.Public | BindingFlags.Instance));
        }
        foreach (var member in new[] { "TryGetTrigger", "Join", "IsConsistentWith", "Serialize" })
        {
            Assert.NotNull(typeof(CausalityStamp).GetMethod(member, BindingFlags.Public | BindingFlags.Instance));
        }
        Assert.NotNull(typeof(CausalityStamp).GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static));

        // Stamp creation stays with the graph: the sync layer reads, compares and transports
        // stamps but never mints them.
        Assert.Null(typeof(CausalityStamp).GetMethod("ForSignal", BindingFlags.Public | BindingFlags.Static));
        var implementsStamped = typeof(MemoBase<int>).GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStampedGetR<>));
        Assert.True(implementsStamped);
    }
}
