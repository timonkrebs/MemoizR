using MemoizR.Distributed;
using Microsoft.Extensions.Time.Testing;

namespace MemoizR.Tests;

// MemoizR.Distributed v0: exports, remote-signal adoption (sequence ordering, epochs,
// unverifiability) and the self-healing glitch barrier, wired over in-proc delegates.
public class DistributedPackageTests
{
    // ── export side ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_AdvertisesOnValueChange_AndPullAnswersTheSamePublication()
    {
        var host = new MemoFactory();
        var temperature = host.CreateSignal(20.0);
        var comfort = host.CreateMemoizR("comfort", async () => await temperature.Get() + 1);

        var advertisements = new List<StaleNotification>();
        using var export = host.Export(comfort, n =>
        {
            lock (advertisements) { advertisements.Add(n); }
            return Task.CompletedTask;
        });

        await temperature.Set(25.0);
        await TestHelpers.WaitForConvergenceAsync(() => { lock (advertisements) { return advertisements.Count > 0; } });

        var payload = await export.PullAsync();
        Assert.Equal(comfort.Id, payload.NodeId);
        Assert.Equal(26.0, payload.Value);
        Assert.NotEqual(0, payload.Epoch);
        Assert.True(payload.Sequence >= 1);
        Assert.False(payload.Unverifiable);

        // The advertised stamp is a valid v2 payload describing the publication.
        var stamp = CausalityStamp.Deserialize(payload.Stamp);
        Assert.True(stamp.TryGetTrigger(temperature.Id, out var trigger));
        Assert.Equal(1, trigger);
    }

    [Fact]
    public async Task Export_SequencesStrictlyIncreasePerPublication()
    {
        var host = new MemoFactory();
        var s = host.CreateSignal(0);
        using var export = host.Export(s, _ => Task.CompletedTask);

        var first = await export.PullAsync();
        await s.Set(1);
        var second = await export.PullAsync();
        await s.Set(2);
        var third = await export.PullAsync();

        Assert.True(first.Sequence < second.Sequence);
        Assert.True(second.Sequence < third.Sequence);
    }

    [Fact]
    public void Export_FromStampsDisabledContext_Throws()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.DisableCausalityStamps);
        var s = f.CreateSignal(1);
        Assert.Throws<InvalidOperationException>(() => f.Export(s, _ => Task.CompletedTask));
    }

    [Fact]
    public void Export_NodeOfAnotherContext_Throws()
    {
        var f1 = new MemoFactory();
        var f2 = new MemoFactory();
        var s = f1.CreateSignal(1);
        Assert.Throws<ArgumentException>(() => f2.Export(s, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Export_Heartbeat_ReadvertisesOnTheClock()
    {
        var host = new MemoFactory();
        var s = host.CreateSignal(1);
        var count = 0;
        using var export = host.Export(s, _ => { Interlocked.Increment(ref count); return Task.CompletedTask; });

        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref count) > 0); // initial eager advertisement
        var baseline = Volatile.Read(ref count);

        var clock = new FakeTimeProvider();
        export.StartHeartbeat(TimeSpan.FromSeconds(30), clock);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(baseline + 1, Volatile.Read(ref count));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(baseline + 3, Volatile.Read(ref count));
    }

    // ── consumer side: adoption ordering ─────────────────────────────────────────────────

    [Fact]
    public async Task RemoteSignal_AdoptsInOrder_AndDropsLateAndDuplicateDeliveries()
    {
        var host = new MemoFactory();
        var s = host.CreateSignal(10);
        using var export = host.Export(s, _ => Task.CompletedTask);
        var oldPayload = await export.PullAsync();
        await s.Set(20);
        var newPayload = await export.PullAsync();

        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0, export.PullAsync);

        await mirror.OnValueAsync(newPayload);
        Assert.Equal(20, await mirror.Local.Get());

        // A late delivery of the older publication and a duplicate of the current one are both
        // dropped by the sequence order -- no stamp comparison needed.
        await mirror.OnValueAsync(oldPayload);
        Assert.Equal(20, await mirror.Local.Get());
        await mirror.OnValueAsync(newPayload);
        Assert.Equal(20, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_SequenceOrdersWhatStampsCannot()
    {
        // A dependency set oscillating through empty re-publishes an EARLIER stamp on a NEWER
        // value; the per-node sequence still orders the publications totally.
        var host = new MemoFactory();
        var s = host.CreateSignal(1);
        var epoch = 7L;
        var stamp = CausalityStamp.ForSignal(s.Id, 1, epoch).Serialize();
        var emptyStamp = CausalityStamp.Empty.Serialize();

        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0, () => throw new InvalidOperationException("no pull in this test"));

        await mirror.OnValueAsync(new ValuePayload<int>(s.Id, epoch, 1, 100, stamp, false));
        await mirror.OnValueAsync(new ValuePayload<int>(s.Id, epoch, 2, 200, emptyStamp, false)); // honest empty publication
        await mirror.OnValueAsync(new ValuePayload<int>(s.Id, epoch, 3, 300, stamp, false));      // same stamp as seq 1, NEWER value

        Assert.Equal(300, await mirror.Local.Get());

        // The late redelivery of seq 1 must not roll the mirror back, even though its stamp
        // equals the currently held one.
        await mirror.OnValueAsync(new ValuePayload<int>(s.Id, epoch, 1, 100, stamp, false));
        Assert.Equal(300, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_StaleHandler_PullsOnlyWhenNotProvablyOld()
    {
        var host = new MemoFactory();
        var s = host.CreateSignal(1);
        using var export = host.Export(s, _ => Task.CompletedTask);
        var payload = await export.PullAsync();

        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0, async () =>
        {
            Interlocked.Increment(ref pulls);
            return await export.PullAsync();
        });

        // Advertisement of an unknown epoch: pull (first contact).
        await mirror.OnStaleAsync(new StaleNotification(payload.NodeId, payload.Epoch, payload.Sequence, payload.Stamp));
        Assert.Equal(1, Volatile.Read(ref pulls));
        Assert.Equal(1, await mirror.Local.Get());

        // The same advertisement again: provably old news (same epoch, sequence not above the
        // adopted one) -- no pull.
        await mirror.OnStaleAsync(new StaleNotification(payload.NodeId, payload.Epoch, payload.Sequence, payload.Stamp));
        Assert.Equal(1, Volatile.Read(ref pulls));

        // A higher sequence: pull.
        await s.Set(2);
        var newer = await export.PullAsync();
        Volatile.Write(ref pulls, 0);
        await mirror.OnStaleAsync(new StaleNotification(newer.NodeId, newer.Epoch, newer.Sequence, newer.Stamp));
        Assert.Equal(1, Volatile.Read(ref pulls));
        Assert.Equal(2, await mirror.Local.Get());
    }

    // ── consumer side: resets and unverifiability ────────────────────────────────────────

    [Fact]
    public async Task RemoteSignal_PeerReset_DiscardsEvidence_RunsHook_AndDropsDeadEpochTraffic()
    {
        var epoch1 = 11L;
        var epoch2 = 22L;
        var stamp1 = CausalityStamp.ForSignal(1000, 5, epoch1).Serialize();
        var stamp2 = CausalityStamp.ForSignal(1000, 1, epoch2).Serialize();

        var resets = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => throw new InvalidOperationException("no pull in this test"),
            onPeerReset: () => { Interlocked.Increment(ref resets); return Task.CompletedTask; });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 9, 111, stamp1, false));
        Assert.Equal(111, await mirror.Local.Get());

        // The restarted peer's sequence starts over BELOW the old one: the epoch change, not
        // the sequence, is what admits it.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, stamp2, false));
        Assert.Equal(222, await mirror.Local.Get());
        Assert.Equal(1, Volatile.Read(ref resets));
        Assert.Equal(epoch2, mirror.RemoteStamp.Epoch);

        // Late traffic from the dead incarnation -- even with a huge sequence -- is dropped.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 999, 333, stamp1, false));
        Assert.Equal(222, await mirror.Local.Get());
        Assert.Equal(1, Volatile.Read(ref resets));
    }

    [Fact]
    public async Task RemoteSignal_UnverifiablePayload_BlocksTrust_AndAHigherSequenceHeals()
    {
        var epoch = 5L;
        var verified = CausalityStamp.ForSignal(1000, 1, epoch).Serialize();
        var empty = CausalityStamp.Empty.Serialize();

        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => throw new InvalidOperationException("no pull in this test"));

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch, 1, 10, verified, false));
        Assert.False(mirror.Unverifiable);
        var heldStamp = mirror.RemoteStamp;

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch, 2, 11, empty, true));
        Assert.True(mirror.Unverifiable);
        Assert.True(mirror.HasEvidence);
        Assert.Equal(11, await mirror.Local.Get());
        // The held stamp of the last VERIFIED adoption is kept for the barrier.
        Assert.Equal(heldStamp, mirror.RemoteStamp);

        // A late verified delivery from BEFORE the unverifiable spell must not fake a heal.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch, 1, 10, verified, false));
        Assert.True(mirror.Unverifiable);

        // The recovery is a NEW publication: higher sequence, verified.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch, 3, 12, verified, false));
        Assert.False(mirror.Unverifiable);
        Assert.Equal(12, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_RejectsMalformedAndEpochlessPayloads()
    {
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => throw new InvalidOperationException("no pull in this test"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => mirror.OnValueAsync(new ValuePayload<int>(1, 1, 1, 1, [0xFF, 0xFF], false)));
        await Assert.ThrowsAsync<ArgumentException>(
            () => mirror.OnValueAsync(new ValuePayload<int>(1, 0, 1, 1, CausalityStamp.Empty.Serialize(), false)));

        // A non-empty stamp whose epoch differs from the header's is a protocol violation: the
        // ordering and the evidence would describe different incarnations.
        var mismatched = CausalityStamp.ForSignal(1, 1, epoch: 7).Serialize();
        await Assert.ThrowsAsync<ArgumentException>(
            () => mirror.OnValueAsync(new ValuePayload<int>(1, 5, 1, 1, mismatched, false)));
    }

    // ── the glitch barrier ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Barrier_DetectsGlitch_RepullsLagging_AndRendersConsistentSnapshot()
    {
        // Host: two derived nodes over the same signal; consumer mirrors both. Deliver only one
        // mirror's update after a write: the barrier must not render the torn pair, re-pull the
        // lagging mirror itself, and render the consistent snapshot.
        var host = new MemoFactory();
        var temperature = host.CreateSignal(20.0);
        var dew = host.CreateMemoizR("dew", async () => await temperature.Get() - 5);
        var heat = host.CreateMemoizR("heat", async () => await temperature.Get() + 5);

        using var dewExport = host.Export(dew, _ => Task.CompletedTask);
        using var heatExport = host.Export(heat, _ => Task.CompletedTask);

        var consumer = new MemoFactory();
        var dewMirror = consumer.CreateRemoteSignal("dew", 0.0, dewExport.PullAsync);
        var heatMirror = consumer.CreateRemoteSignal("heat", 0.0, heatExport.PullAsync);

        var renders = new List<(double Dew, double Heat)>();
        var reaction = DistributedBarrier.CreateConsistentReaction(
            consumer, dewMirror, heatMirror,
            (d, h) => { lock (renders) { renders.Add((d, h)); } });

        // Initial sync on the pre-write snapshot.
        await dewMirror.PullAsync();
        await heatMirror.PullAsync();
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((15.0, 25.0)); } });

        // The write; only dew's fresh publication is delivered -- heat is now lagging.
        await temperature.Set(30.0);
        await dewMirror.OnValueAsync(await dewExport.PullAsync());

        // The barrier re-pulls heat itself and renders the healed snapshot.
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((25.0, 35.0)); } });

        // The torn pairs (fresh dew with stale heat, or the reverse) must never have rendered.
        lock (renders)
        {
            Assert.DoesNotContain((25.0, 25.0), renders);
            Assert.DoesNotContain((15.0, 35.0), renders);
        }
        GC.KeepAlive(reaction);
    }

    [Fact]
    public async Task Barrier_SkipsRendering_WhileAMirrorIsUnverifiable()
    {
        var epoch = 5L;
        var stamp = CausalityStamp.ForSignal(1000, 1, epoch);
        var consumer = new MemoFactory();
        var a = consumer.CreateRemoteSignal<int>("a", 0, () => throw new InvalidOperationException("no pull"));
        var b = consumer.CreateRemoteSignal<int>("b", 0, () => throw new InvalidOperationException("no pull"));

        var renders = 0;
        var reaction = DistributedBarrier.CreateConsistentReaction(
            consumer, a, b, (_, _) => Interlocked.Increment(ref renders));

        await a.OnValueAsync(new ValuePayload<int>(1000, epoch, 1, 1, stamp.Serialize(), false));
        await b.OnValueAsync(new ValuePayload<int>(1001, epoch, 1, 2, CausalityStamp.Empty.Serialize(), true));

        // Give the debounced reaction time to run: it must skip, not render a half-trusted pair.
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref renders));

        // The heal: b's host publishes verified again.
        await b.OnValueAsync(new ValuePayload<int>(1001, epoch, 2, 3, CausalityStamp.ForSignal(1001, 1, epoch).Serialize(), false));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref renders) > 0);
        GC.KeepAlive(reaction);
    }
}
