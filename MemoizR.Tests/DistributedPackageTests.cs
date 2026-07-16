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
    public void Export_OfAMirrorsLocalSignal_Throws()
    {
        // Re-exporting a mirror would advertise its LOCAL adoption stamps as evidence (the
        // origin's evidence lives beside the graph in Publication): two re-exported mirrors
        // of one origin host carry disjoint local stamps, so a downstream barrier would
        // render torn origin snapshots as consistent. Multi-hop bridging is wire-v3 work.
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => throw new InvalidOperationException("no pull in this test"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => consumer.Export(mirror.Local, _ => Task.CompletedTask));
        Assert.Contains("re-exported", ex.Message);

        // An ordinary eager signal of the same runtime type still exports.
        var plain = consumer.CreateEagerRelativeSignal("plain", 0);
        using var export = consumer.Export(plain, _ => Task.CompletedTask);
    }

    [Fact]
    public async Task Export_ValueTypeSignalThroughItsStampedInterface_Exports()
    {
        // A value-type Signal<T>'s stamped interface is IStampedGetR<T> at runtime (the T? in
        // its declaration is a nullable ANNOTATION on the unconstrained parameter, not
        // Nullable<T>), so generic bridge code holding the interface exports through the
        // ordinary generic overload -- pinned here so the interface path stays supported.
        var host = new MemoFactory();
        var s = host.CreateSignal(5);
        IStampedGetR<int> node = s;
        using var export = host.Export(node, _ => Task.CompletedTask);
        Assert.Equal(s.Id, export.NodeId);

        var payload = await export.PullAsync();
        Assert.Equal(5, payload.Value);
        Assert.False(payload.Unverifiable);

        await s.Set(6);
        var newer = await export.PullAsync();
        Assert.Equal(6, newer.Value);
        Assert.True(newer.Sequence > payload.Sequence);
    }

    [Fact]
    public async Task Export_OfANodeDerivedFromAMirror_Throws()
    {
        // A memo over a mirror carries stamps captured from the consumer-local trigger, not
        // the origin's evidence: re-exporting it is the mirror re-export unsoundness one hop
        // removed.
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => throw new InvalidOperationException("no pull in this test"));

        var derived = consumer.CreateMemoizR("derived", async () => await mirror.Local.Get() + 1);
        await derived.Get(); // wire the sources
        Assert.Throws<InvalidOperationException>(() => consumer.Export(derived, _ => Task.CompletedTask));

        // A LAZY memo has no wired sources at export time and passes the fail-fast check --
        // the wire egress catches it: the pull's own read wires the chain before the check.
        var lazyDerived = consumer.CreateMemoizR("lazy", async () => await mirror.Local.Get() + 2);
        using var export = consumer.Export(lazyDerived, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => export.PullAsync());
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

        // The publish is detached from the timer callback's flow, so each tick's advertisement
        // lands asynchronously.
        var clock = new FakeTimeProvider();
        export.StartHeartbeat(TimeSpan.FromSeconds(30), clock);
        clock.Advance(TimeSpan.FromSeconds(30));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref count) == baseline + 1);
        clock.Advance(TimeSpan.FromSeconds(60));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref count) == baseline + 3);
    }

    [Fact]
    public async Task Export_Pull_RefreshesAStickyUnverifiablePublication()
    {
        // Unverifiability can outlive its cause: a memo that catches a faulted dependency
        // publishes its fallback with no-claim evidence and commits CLEAN (a fault is not a
        // Set; nothing bumped its generation), and nothing ever re-dirties it once the
        // dependency heals without a value change. Served on a pull, the consumer's mirror
        // drops the unchanged publication as a duplicate and stays unverifiable forever (the
        // barrier never renders). A pull must instead force a fresh evaluation of the
        // unverifiable chain and answer with the host's current best claim.
        var host = new MemoFactory();
        var s = host.CreateSignal(1);
        var trigger = host.CreateSignal(0);
        var failing = false;
        var m1 = host.CreateMemoizR("m1", async () =>
        {
            var v = await s.Get();
            if (Volatile.Read(ref failing))
            {
                throw new InvalidOperationException("m1 source down");
            }
            return v - v + 11; // always 11, still depends on s
        });
        var m2 = host.CreateMemoizR("m2", async () =>
        {
            var t = await trigger.Get();
            try
            {
                return t + await m1.Get() + 100;
            }
            catch (InvalidOperationException)
            {
                return 999; // the caught-fault fallback: published as unverifiable
            }
        });
        Assert.Equal(111, await m2.Get());

        // The export is created only AFTER the poisoned state is established: an export
        // reaction chases invalidations on a detached flow, and on a loaded runner it can
        // reach the faulting m1 first -- m1 then falls back to serve-last-good and the test's
        // own m2 evaluation would see a healthy cached m1 instead of catching the fault.
        Volatile.Write(ref failing, true);
        await s.Set(2);       // dirties m1, whose evaluation now faults
        await trigger.Set(1); // dirties m2 directly, so its own computation runs and catches
        Assert.Equal(999, await m2.Get());
        Assert.True(m2.Evidence.Unverifiable);
        Volatile.Write(ref failing, false); // the dependency has healed

        // The sticky shape: m2 committed its fallback CLEAN with no-claim evidence, and
        // nothing is left to re-dirty it.
        using var export = host.Export(m2, _ => Task.CompletedTask);
        var payload = await export.PullAsync();
        Assert.False(payload.Unverifiable);
        Assert.Equal(112, payload.Value); // trigger(1) + healed m1(11) + 100
        Assert.False(m2.Evidence.Unverifiable);

        // The consumer-side heal completes: the fresh publication has a higher sequence, so
        // the mirror adopts it and stops being unverifiable.
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("m2", 0, export.PullAsync);
        await mirror.OnValueAsync(payload);
        Assert.False(mirror.Unverifiable);
        Assert.Equal(112, await mirror.Local.Get());
    }

    [Fact]
    public async Task Export_Pull_NeverServesFaultParkedStateAsCurrentTruth()
    {
        // An already-published export goes CacheCheck and a dependency faults during the
        // parent scan: the read serves the last good VERIFIED box and parks non-clean.
        // Shipping that as current truth would let a new mirror adopt stale state as trusted
        // while the host cannot actually compute a current value -- the pull must force a
        // real re-evaluation instead: a FAULTED pull while broken (the wire contract's
        // honest answer), the fresh verified truth once healed.
        var host = new MemoFactory();
        var s = host.CreateSignal(1);
        var failing = false;
        var m1 = host.CreateMemoizR("m1", async () =>
        {
            var v = await s.Get();
            if (Volatile.Read(ref failing))
            {
                throw new InvalidOperationException("m1 source down");
            }
            return v + 10;
        });
        var m2 = host.CreateMemoizR("m2", async () => await m1.Get() + 100); // no catch: faults propagate

        Assert.Equal(111, await m2.Get()); // published verified, seq 1
        var export = host.Export(m2, _ => Task.CompletedTask);

        // Stop the export's own staleness chase: the pull below models a transport request
        // racing AHEAD of any local read after the write -- the only window in which the
        // fault-park is still observable (a second read's scan launders it to clean-serving-
        // last-good, which the payload then honestly stamps as old).
        export.Dispose();
        Volatile.Write(ref failing, true);
        await s.Set(2); // the chain is dirty; every re-evaluation now faults

        // The pull's own scan suppresses m1's fault and would serve the stale verified 111 as
        // current truth; the refresh detects the non-clean park and forces a REAL
        // re-evaluation, whose fault propagates -- the wire contract's honest answer.
        await Assert.ThrowsAsync<InvalidOperationException>(() => export.PullAsync());

        // Healed and re-published: the pull answers the fresh current truth.
        Volatile.Write(ref failing, false);
        await s.Set(3);
        var healed = await export.PullAsync();
        Assert.False(healed.Unverifiable);
        Assert.Equal(113, healed.Value); // m1 = 3 + 10, m2 = 13 + 100
    }

    [Fact]
    public async Task Export_Pull_ReattemptsFaultParkedDependencies()
    {
        // A dependency that FAULTED keeps its previous verified evidence and parks at
        // CacheCheck, serving its last good value. The unverifiable-chain refresh must force
        // it to genuinely re-evaluate: while the fault persists the pull answers honestly
        // unverifiable, and once healed it answers with the dependency's FRESH value -- never
        // a verified payload built on the stale parked one.
        var host = new MemoFactory();
        var s = host.CreateSignal(1);
        var trigger = host.CreateSignal(0);
        var failing = false;
        var m1 = host.CreateMemoizR("m1", async () =>
        {
            var v = await s.Get();
            if (Volatile.Read(ref failing))
            {
                throw new InvalidOperationException("m1 source down");
            }
            return v + 10; // s-dependent: the parked value goes stale when s moves
        });
        var m2 = host.CreateMemoizR("m2", async () =>
        {
            var t = await trigger.Get();
            try
            {
                return t + await m1.Get() + 100;
            }
            catch (InvalidOperationException)
            {
                return 999;
            }
        });

        Assert.Equal(111, await m2.Get()); // s=1: m1=11, trigger=0

        Volatile.Write(ref failing, true);
        await s.Set(2);       // m1's honest value is now 12 -- but its evaluation faults
        await trigger.Set(1); // m2's own computation runs and catches
        Assert.Equal(999, await m2.Get());
        Assert.True(m2.Evidence.Unverifiable);

        using var export = host.Export(m2, _ => Task.CompletedTask);

        // While the fault persists, the refresh re-attempts the chain and answers honestly.
        var stillBroken = await export.PullAsync();
        Assert.True(stillBroken.Unverifiable);
        Assert.Equal(999, stillBroken.Value);

        // Once healed, the refresh recomputes the dependency itself: trigger(1) + fresh
        // m1(12) + 100 -- never a verified payload built on the stale parked 11.
        Volatile.Write(ref failing, false);
        var healed = await export.PullAsync();
        Assert.False(healed.Unverifiable);
        Assert.Equal(113, healed.Value);
    }

    [Fact]
    public async Task Export_PublishStale_MayFeedASameContextSignal()
    {
        // An in-process bridge that writes advertisements into a same-context outbox signal:
        // the publish must run on a detached flow, or the export reaction's lock scope would
        // flow into the callback and the outbox Set would be refused as a recursive exclusive
        // acquisition -- silently recorded as LastPublishError, losing the advertisement.
        var host = new MemoFactory();
        var s = host.CreateSignal(1);
        var outbox = host.CreateEagerRelativeSignal("outbox", 0L);
        var published = 0;
        using var export = host.Export(s, async n =>
        {
            await outbox.Set(_ => n.Sequence);
            Interlocked.Increment(ref published);
        });

        await s.Set(2);
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref published) > 0);
        Assert.Null(export.LastPublishError);
        Assert.True(await outbox.Get() >= 1);
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

        // The host's current truth, as the mirror's verification pull will see it.
        var hostPayload = new ValuePayload<int>(1000, epoch1, 9, 111, stamp1, false);

        var resets = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => Task.FromResult(hostPayload),
            onPeerReset: () => { Interlocked.Increment(ref resets); return Task.CompletedTask; });

        await mirror.OnValueAsync(hostPayload);
        Assert.Equal(111, await mirror.Local.Get());

        // The restarted peer's sequence starts over BELOW the old one: the epoch change, not
        // the sequence, is what admits it -- committed through the verification pull that
        // answers the unsolicited epoch-mismatch delivery.
        hostPayload = new ValuePayload<int>(1000, epoch2, 1, 222, stamp2, false);
        await mirror.OnValueAsync(hostPayload);
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
    public async Task RemoteSignal_Publication_BindsValueAndEvidenceAtomically()
    {
        // The barrier's contract: one adopted publication is ONE snapshot -- value, header
        // epoch and stamp never observable in a torn combination.
        var epoch = 5L;
        var verified = CausalityStamp.ForSignal(1000, 1, epoch);
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => throw new InvalidOperationException("no pull in this test"));

        Assert.Null(mirror.Publication);

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch, 1, 10, verified.Serialize(), false));
        var publication = mirror.Publication;
        Assert.NotNull(publication);
        Assert.Equal(10, publication.Value);
        Assert.Equal(epoch, publication.Epoch);
        Assert.Equal(verified, publication.Stamp);
        Assert.False(publication.Unverifiable);

        // An unverifiable adoption publishes the NEW value with the HELD verified stamp, still
        // as one snapshot.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch, 2, 11, CausalityStamp.Empty.Serialize(), true));
        publication = mirror.Publication;
        Assert.NotNull(publication);
        Assert.Equal(11, publication.Value);
        Assert.Equal(epoch, publication.Epoch);
        Assert.Equal(verified, publication.Stamp);
        Assert.True(publication.Unverifiable);
    }

    [Fact]
    public async Task RemoteSignal_ThrowingResetHook_LeavesTheValueAdopted_NotWedged()
    {
        // A resubscription hook failure surfaces to the delivering caller, but the adoption
        // itself must have committed: otherwise the ordering state would claim the sequence
        // while the local signal never published, and every redelivery would be dropped as a
        // duplicate -- wedging the mirror on the old value until an unrelated new publication.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var resetPayload = new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false);
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => Task.FromResult(resetPayload),
            onPeerReset: () => throw new InvalidOperationException("resubscribe transport down"));

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mirror.OnValueAsync(resetPayload));

        // The reset payload WAS adopted; only the resubscription failed.
        Assert.Equal(222, await mirror.Local.Get());
        Assert.Equal(epoch2, mirror.RemoteStamp.Epoch);

        // Its redelivery is an ordinary duplicate (no reset, no hook, no throw) ...
        await mirror.OnValueAsync(resetPayload);
        Assert.Equal(222, await mirror.Local.Get());

        // ... and newer publications keep flowing.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 2, 333, CausalityStamp.ForSignal(1000, 2, epoch2).Serialize(), false));
        Assert.Equal(333, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_ResetHook_MayPullTheSameMirror()
    {
        // The documented resubscribe path: a bridge answering a reset by pulling the mirror's
        // current truth. The hook runs outside the adoption gate, so the nested pull re-enters
        // the adoption path instead of deadlocking on the gate.
        var epoch1 = 11L;
        var epoch2 = 22L;
        RemoteSignal<int>? mirror = null;
        var consumer = new MemoFactory();
        mirror = consumer.CreateRemoteSignal("mirror", 0,
            () => Task.FromResult(new ValuePayload<int>(1000, epoch2, 2, 999, CausalityStamp.ForSignal(1000, 2, epoch2).Serialize(), false)),
            onPeerReset: () => mirror!.PullAsync());

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        // A deadlock here must fail the test, not hang the suite.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(999, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_StaleEpochAnswer_DuringResubscription_CannotAbandonTheJustAdoptedEpoch()
    {
        // While a reset's OnPeerReset resubscription is in flight, the pull channel may still
        // point at the dead incarnation. An epoch-changing answer from a pull issued inside
        // that window must be refused: committing it would abandon the epoch the mirror JUST
        // adopted and wedge it on a dead incarnation.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var deadEpoch = 33L;
        var hookStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0,
            () =>
            {
                var n = Interlocked.Increment(ref pulls);
                return Task.FromResult(n switch
                {
                    // The verification pull that commits the reset to epoch2.
                    1 => new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false),
                    // The not-yet-resubscribed channel answers a window-issued pull with a
                    // dead incarnation's payload.
                    2 => new ValuePayload<int>(1000, deadEpoch, 9, 999, CausalityStamp.ForSignal(1000, 9, deadEpoch).Serialize(), false),
                    // The live truth, after the window closed.
                    _ => new ValuePayload<int>(1000, epoch2, 3, 333, CausalityStamp.ForSignal(1000, 3, epoch2).Serialize(), false),
                });
            },
            onPeerReset: async () =>
            {
                hookStarted.TrySetResult();
                await releaseHook.Task;
            });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        // Unsolicited epoch change -> verification pull commits epoch2 -> the hook starts.
        var delivery = mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        await hookStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A pull issued inside the resubscription window: its dead-incarnation answer is
        // refused, and the just-adopted live epoch survives.
        await mirror.PullAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(epoch2, mirror.Publication!.Epoch);
        Assert.Equal(222, await mirror.Local.Get());

        releaseHook.SetResult();
        await delivery.WaitAsync(TimeSpan.FromSeconds(10));

        // The mirror is not stuck: the live epoch keeps flowing after the window closes.
        await mirror.OnStaleAsync(new StaleNotification(1000, epoch2, 3, CausalityStamp.Empty.Serialize()));
        Assert.Equal(333, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_EpochChangeRefusedDuringResubscription_IsVerifiedWhenTheWindowCloses()
    {
        // The peer restarts AGAIN while the previous reset's resubscription hook is still
        // running. The mid-window delivery is refused (it may have travelled the dead
        // channel) -- but it may have been the new incarnation's ONLY advertisement, so
        // closing the window must answer it with one fresh verification pull instead of
        // pinning the mirror to the now-dead epoch. Hook runs stay strictly sequential.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var epoch3 = 33L;
        var releaseFirstHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRuns = 0;
        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0,
            () =>
            {
                var n = Interlocked.Increment(ref pulls);
                return Task.FromResult(n == 1
                    // The verification pull that commits the first reset (epoch1 -> epoch2).
                    ? new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false)
                    // Every later pull answers the live truth: the second restart's incarnation.
                    : new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false));
            },
            onPeerReset: async () =>
            {
                if (Interlocked.Increment(ref hookRuns) == 1)
                {
                    await releaseFirstHook.Task;
                }
            });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        // First restart: the unsolicited delivery triggers the verification pull, the reset
        // commits, and hook 1 blocks -- the resubscription window is open.
        var delivery = mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref hookRuns) == 1);

        // Second restart mid-resubscription: its single-shot delivery is refused (and no
        // second hook starts -- windows are single-flight), but remembered.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false));
        Assert.Equal(epoch2, mirror.Publication!.Epoch);
        Assert.Equal(1, Volatile.Read(ref hookRuns));

        releaseFirstHook.SetResult();
        await delivery.WaitAsync(TimeSpan.FromSeconds(10));

        // Closing the window verifies the refused epoch change by pull and adopts the live
        // incarnation, running its hook in sequence.
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref hookRuns) == 2);
        Assert.Equal(epoch3, mirror.Publication!.Epoch);
        Assert.Equal(333, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_FailedLocalWrite_DoesNotAdvanceTheOrdering()
    {
        // A bridge that (wrongly) delivers from inside a same-context reaction flow: the
        // local write is refused as a recursive acquisition and the delivery faults. The
        // ordering state must not have advanced -- the redelivery from a proper transport
        // flow must adopt, instead of being duplicate-dropped against a sequence the graph
        // never published.
        var epoch = 11L;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal<int>("mirror", 0,
            () => throw new InvalidOperationException("no pull in this test"));
        var payload = new ValuePayload<int>(1000, epoch, 1, 111, CausalityStamp.ForSignal(1000, 1, epoch).Serialize(), false);

        var poke = consumer.CreateSignal(0);
        Task? adoptInsideReaction = null;
        var reactionRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reaction = consumer.BuildReaction().CreateReaction(poke, _ =>
        {
            adoptInsideReaction ??= mirror.OnValueAsync(payload);
            reactionRan.TrySetResult();
        });

        await reactionRan.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adoptInsideReaction!);
        Assert.False(mirror.HasEvidence);

        // The redelivery from a clean flow adopts.
        await mirror.OnValueAsync(payload);
        Assert.Equal(111, await mirror.Local.Get());
        Assert.Equal(epoch, mirror.Publication!.Epoch);
        GC.KeepAlive(reaction);
    }

    [Fact]
    public async Task RemoteSignal_SupersededNewerRestartAnswer_IsReverified_NotDropped()
    {
        // Two racing verification pulls under one generation: the OLDER restart's answer
        // commits first (it was live when it answered), superseding the newer restart's
        // answer. Dropping that answer silently would pin the mirror to the now-dead epoch
        // when its delivery was a one-shot -- it must re-enter the verification path instead.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var epoch3 = 33L;
        var firstAnswer = new TaskCompletionSource<ValuePayload<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAnswer = new TaskCompletionSource<ValuePayload<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0, () =>
            Interlocked.Increment(ref pulls) switch
            {
                1 => firstAnswer.Task,
                2 => secondAnswer.Task,
                // The re-verification and anything later answers the live incarnation.
                _ => Task.FromResult(new ValuePayload<int>(1000, epoch3, 2, 333, CausalityStamp.ForSignal(1000, 2, epoch3).Serialize(), false)),
            });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        // Two one-shot deliveries from two successive restarts, each triggering a pull.
        var delivery2 = mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        var delivery3 = mirror.OnValueAsync(new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false));

        // The older restart's answer arrives first and commits (it was live at answer time)...
        firstAnswer.SetResult(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        await delivery2.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(epoch2, mirror.Publication!.Epoch);

        // ... superseding the newer restart's answer, which must be re-verified, not dropped.
        secondAnswer.SetResult(new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false));
        await delivery3.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(epoch3, mirror.Publication!.Epoch);
        Assert.Equal(333, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_AfterFailedResubscription_StaleAnswersDoNotSelfSolicit()
    {
        // A pull issued during the resubscription window is still in flight when the hook
        // FAILS. Its delayed answer arrives superseded (the close bumped the generation) --
        // re-verifying it would actively pull the channel just reported broken, and a dead
        // epoch answered by that fresh-generation pull could commit. While the channel is
        // suspect, superseded stale answers are dropped without self-solicitation; a real
        // delivery (the repaired bridge's advertisement) still verifies and heals normally.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var epoch3 = 33L;
        var deadEpoch = 99L;
        var failHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var windowAnswer = new TaskCompletionSource<ValuePayload<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRuns = 0;
        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0,
            () =>
            {
                var n = Interlocked.Increment(ref pulls);
                return n switch
                {
                    1 => Task.FromResult(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false)),
                    2 => windowAnswer.Task,
                    _ => Task.FromResult(new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false)),
                };
            },
            onPeerReset: async () =>
            {
                if (Interlocked.Increment(ref hookRuns) == 1)
                {
                    await failHook.Task;
                    throw new InvalidOperationException("resubscribe transport down");
                }
            });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        var delivery = mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref hookRuns) == 1);

        // A pull issued inside the window, still awaiting its answer when the hook fails.
        var windowPull = mirror.PullAsync();
        failHook.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.WaitAsync(TimeSpan.FromSeconds(10)));

        // The delayed in-window answer (a dead incarnation) arrives superseded: dropped, and
        // NOT re-verified -- no self-initiated pull touches the broken channel.
        windowAnswer.SetResult(new ValuePayload<int>(1000, deadEpoch, 7, 999, CausalityStamp.ForSignal(1000, 7, deadEpoch).Serialize(), false));
        await windowPull.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref pulls));
        Assert.Equal(epoch2, mirror.Publication!.Epoch);

        // The repaired bridge's advertisement heals normally (and re-trusts the channel).
        await mirror.OnStaleAsync(new StaleNotification(1000, epoch3, 1, CausalityStamp.Empty.Serialize()));
        Assert.Equal(epoch3, mirror.Publication!.Epoch);
        Assert.Equal(333, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_RestartAdvertisedDuringResubscription_IsVerifiedWhenTheWindowCloses()
    {
        // A restart ADVERTISEMENT arrives mid-window. Chasing it immediately would pull the
        // not-yet-resubscribed channel, whose answer (the old epoch -- a harmless duplicate)
        // sets no pending verification: the single-shot advert would be lost and the mirror
        // pinned to the dead incarnation. The advert must be remembered and followed up by
        // the close's verification pull on the repaired channel.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var epoch3 = 33L;
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channelRepaired = false;
        var hookRuns = 0;
        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0,
            () =>
            {
                Interlocked.Increment(ref pulls);
                // The un-resubscribed channel answers the CURRENT epoch; the repaired channel
                // answers the live restarted incarnation.
                return Task.FromResult(Volatile.Read(ref channelRepaired)
                    ? new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false)
                    : new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
            },
            onPeerReset: async () =>
            {
                if (Interlocked.Increment(ref hookRuns) == 1)
                {
                    await releaseHook.Task;
                    Volatile.Write(ref channelRepaired, true); // resubscription repairs the channel
                }
            });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        // First restart: the verification pull commits epoch2, hook 1 blocks (window open).
        var delivery = mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref hookRuns) == 1);
        var pullsBeforeAdvert = Volatile.Read(ref pulls);

        // The second restart's single-shot advertisement mid-window: remembered, NOT chased.
        await mirror.OnStaleAsync(new StaleNotification(1000, epoch3, 1, CausalityStamp.Empty.Serialize()));
        Assert.Equal(pullsBeforeAdvert, Volatile.Read(ref pulls));

        releaseHook.SetResult();
        await delivery.WaitAsync(TimeSpan.FromSeconds(10));

        // The close's verification pull runs on the repaired channel and adopts the live epoch.
        await TestHelpers.WaitForConvergenceAsync(() => mirror.Publication?.Epoch == epoch3);
        Assert.Equal(333, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_QueuedVerificationHookFailure_IsRecorded()
    {
        // The queued verification pull after a window close can commit ANOTHER reset; if
        // that reset's OnPeerReset fails there is no delivering caller to surface to -- the
        // failure must land in LastBackgroundError instead of vanishing (the value is still
        // adopted, like every hook failure).
        var epoch1 = 11L;
        var epoch2 = 22L;
        var epoch3 = 33L;
        var releaseFirstHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRuns = 0;
        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0,
            () =>
            {
                var n = Interlocked.Increment(ref pulls);
                return Task.FromResult(n == 1
                    ? new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false)
                    : new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false));
            },
            onPeerReset: async () =>
            {
                if (Interlocked.Increment(ref hookRuns) == 1)
                {
                    await releaseFirstHook.Task;
                    return;
                }
                throw new InvalidOperationException("resubscribe to the newest incarnation failed");
            });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        var delivery = mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref hookRuns) == 1);

        // A third incarnation's one-shot delivery is refused mid-window and remembered.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false));

        releaseFirstHook.SetResult(); // hook 1 succeeds; the close queues the verification
        await delivery.WaitAsync(TimeSpan.FromSeconds(10));

        // The queued verification adopts epoch3; its hook throws; the failure is recorded.
        await TestHelpers.WaitForConvergenceAsync(() => mirror.LastBackgroundError is InvalidOperationException);
        Assert.Equal(epoch3, mirror.Publication!.Epoch);
        Assert.Equal(333, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_FailedResubscription_DoesNotSolicitTheBrokenChannel()
    {
        // The peer restarts again mid-resubscription AND the hook then fails: the channel was
        // just reported broken, so closing the window must NOT actively pull it -- soliciting
        // it could commit exactly the stale answers the window exists to refuse. Recovery is
        // the bridge's own retry plus the next advertisement, which heals normally.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var epoch3 = 33L;
        var failFirstHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRuns = 0;
        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0,
            () =>
            {
                var n = Interlocked.Increment(ref pulls);
                return Task.FromResult(n == 1
                    ? new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false)
                    : new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false));
            },
            onPeerReset: async () =>
            {
                if (Interlocked.Increment(ref hookRuns) == 1)
                {
                    await failFirstHook.Task;
                    throw new InvalidOperationException("resubscribe transport down");
                }
            });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        var delivery = mirror.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref hookRuns) == 1);

        // A second restart's one-shot delivery is refused during the window...
        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch3, 1, 333, CausalityStamp.ForSignal(1000, 1, epoch3).Serialize(), false));

        // ... and the hook then FAILS: the window closes without soliciting the broken channel.
        failFirstHook.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(100); // no detached verification pull may sneak in
        Assert.Equal(1, Volatile.Read(ref pulls));
        Assert.Equal(epoch2, mirror.Publication!.Epoch);

        // The bridge repairs its channel; the next advertisement heals the mirror normally.
        await mirror.OnStaleAsync(new StaleNotification(1000, epoch3, 1, CausalityStamp.Empty.Serialize()));
        Assert.Equal(epoch3, mirror.Publication!.Epoch);
        Assert.Equal(333, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_DelayedPayloadFromASkippedIncarnation_CannotAbandonTheLiveEpoch()
    {
        // The host restarted twice and the mirror never saw the middle incarnation, so that
        // epoch is not in the abandoned set -- and with unordered random epochs, a delayed
        // payload from it is indistinguishable by inspection from a live restart. Adopting it
        // directly would abandon the LIVE epoch and drop all of its future traffic, wedging
        // the mirror on a dead incarnation. Epoch changes therefore commit only through the
        // mirror's own latest pull, which answers with the live incarnation's truth.
        var deadEpoch = 22L;
        var liveEpoch = 33L;
        var hostSequence = 2L;
        var hostValue = 999;

        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0, () =>
        {
            Interlocked.Increment(ref pulls);
            return Task.FromResult(new ValuePayload<int>(
                1000, liveEpoch, hostSequence, hostValue, CausalityStamp.ForSignal(1000, hostSequence, liveEpoch).Serialize(), false));
        });

        await mirror.OnValueAsync(new ValuePayload<int>(1000, liveEpoch, 1, 300, CausalityStamp.ForSignal(1000, 1, liveEpoch).Serialize(), false));
        Assert.Equal(300, await mirror.Local.Get());

        // The delayed dead-incarnation payload: discarded, answered by one verification pull.
        await mirror.OnValueAsync(new ValuePayload<int>(1000, deadEpoch, 7, 200, CausalityStamp.ForSignal(1000, 7, deadEpoch).Serialize(), false));
        Assert.Equal(1, Volatile.Read(ref pulls));
        Assert.Equal(999, await mirror.Local.Get()); // the pull's answer, never the dead value
        Assert.Equal(liveEpoch, mirror.Publication!.Epoch);

        // The live epoch keeps flowing -- the mirror is not wedged.
        hostSequence = 3;
        hostValue = 1000;
        await mirror.OnStaleAsync(new StaleNotification(1000, liveEpoch, 3, CausalityStamp.Empty.Serialize()));
        Assert.Equal(1000, await mirror.Local.Get());
    }

    [Fact]
    public async Task RemoteSignal_FailedNewerPull_DoesNotStrandAnOlderPullsLiveAnswer()
    {
        // Two verification pulls overlap and the newer one faults. The older pull's answer
        // still carries the live incarnation's truth, and no epoch change committed since it
        // was issued, so it must commit -- invalidating older pulls optimistically at issue
        // time would defer this recovery to an unrelated heartbeat or advertisement.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var firstAnswer = new TaskCompletionSource<ValuePayload<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulls = 0;
        var consumer = new MemoFactory();
        var mirror = consumer.CreateRemoteSignal("mirror", 0, () =>
            Interlocked.Increment(ref pulls) == 1
                ? firstAnswer.Task
                : throw new InvalidOperationException("transport blip"));

        await mirror.OnValueAsync(new ValuePayload<int>(1000, epoch1, 5, 111, CausalityStamp.ForSignal(1000, 5, epoch1).Serialize(), false));

        var olderPull = mirror.PullAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mirror.PullAsync());

        // The host restarted; the older pull's answer reflects the live incarnation.
        firstAnswer.SetResult(new ValuePayload<int>(1000, epoch2, 1, 222, CausalityStamp.ForSignal(1000, 1, epoch2).Serialize(), false));
        await olderPull.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(222, await mirror.Local.Get());
        Assert.Equal(epoch2, mirror.Publication!.Epoch);
    }

    [Fact]
    public async Task RemoteSignal_RejectsPayloadsRoutedToTheWrongMirror()
    {
        // On a multiplexed bridge, a payload routed to the wrong mirror must not adopt: it
        // would replace this mirror's value with a sibling export's and advance the sequence
        // order with the sibling's counter, so the intended node's next payloads would drop as
        // old news.
        var epoch = 5L;
        var consumer = new MemoFactory();

        var pulls = 0;
        var bound = consumer.CreateRemoteSignal("bound", 0,
            () =>
            {
                Interlocked.Increment(ref pulls);
                return Task.FromResult(new ValuePayload<int>(1000, epoch, 9, 9, CausalityStamp.ForSignal(1000, 9, epoch).Serialize(), false));
            },
            nodeId: 1000);

        await Assert.ThrowsAsync<ArgumentException>(
            () => bound.OnValueAsync(new ValuePayload<int>(2000, epoch, 1, 5, CausalityStamp.ForSignal(2000, 1, epoch).Serialize(), false)));
        Assert.False(bound.HasEvidence);

        await bound.OnValueAsync(new ValuePayload<int>(1000, epoch, 1, 10, CausalityStamp.ForSignal(1000, 1, epoch).Serialize(), false));
        Assert.Equal(10, await bound.Local.Get());

        // A foreign advertisement on a broadcast bus is ignored, not treated as an error --
        // and it must not trigger a pull.
        await bound.OnStaleAsync(new StaleNotification(2000, epoch, 50, CausalityStamp.Empty.Serialize()));
        Assert.Equal(0, Volatile.Read(ref pulls));

        // Without an explicit id, the first delivered payload pins the binding.
        var pinned = consumer.CreateRemoteSignal<int>("pinned", 0,
            () => throw new InvalidOperationException("no pull in this test"));
        await pinned.OnValueAsync(new ValuePayload<int>(1000, epoch, 1, 10, CausalityStamp.ForSignal(1000, 1, epoch).Serialize(), false));
        await Assert.ThrowsAsync<ArgumentException>(
            () => pinned.OnValueAsync(new ValuePayload<int>(2000, epoch, 2, 20, CausalityStamp.ForSignal(2000, 2, epoch).Serialize(), false)));
        Assert.Equal(10, await pinned.Local.Get());
    }

    [Fact]
    public async Task Barrier_HostRestartWithEmptyStamp_NeverRendersAcrossIncarnations()
    {
        // A restarted host's first publication can carry an honestly-EMPTY stamp, and empty
        // stamps are vacuously consistent with anything -- the barrier must still refuse to
        // combine it with the other mirror's pre-restart state (the header epoch, not the
        // stamp, carries the incarnation identity), and must re-pull BOTH sides to converge.
        var epoch1 = 11L;
        var epoch2 = 22L;
        var consumer = new MemoFactory();
        var a = consumer.CreateRemoteSignal("a", 0,
            () => Task.FromResult(new ValuePayload<int>(1000, epoch2, 1, 10, CausalityStamp.Empty.Serialize(), false)));
        var b = consumer.CreateRemoteSignal("b", 0,
            () => Task.FromResult(new ValuePayload<int>(1001, epoch2, 1, 20, CausalityStamp.ForSignal(1001, 1, epoch2).Serialize(), false)));

        var renders = new List<(int A, int B)>();
        var reaction = DistributedBarrier.CreateConsistentReaction(
            consumer, a, b, (va, vb) => { lock (renders) { renders.Add((va, vb)); } });

        // Pre-restart snapshot: disjoint verified stamps of the same incarnation.
        await a.OnValueAsync(new ValuePayload<int>(1000, epoch1, 1, 1, CausalityStamp.ForSignal(1000, 1, epoch1).Serialize(), false));
        await b.OnValueAsync(new ValuePayload<int>(1001, epoch1, 1, 2, CausalityStamp.ForSignal(1001, 1, epoch1).Serialize(), false));
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((1, 2)); } });

        // The restart: only mirror a receives the new incarnation's (empty-stamped) truth.
        await a.OnValueAsync(new ValuePayload<int>(1000, epoch2, 1, 10, CausalityStamp.Empty.Serialize(), false));

        // The barrier re-pulls both sides itself and renders the post-restart snapshot.
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((10, 20)); } });

        // Cross-incarnation mixes must never have rendered.
        lock (renders)
        {
            Assert.DoesNotContain((10, 2), renders);
            Assert.DoesNotContain((1, 20), renders);
        }
        GC.KeepAlive(reaction);
    }

    [Fact]
    public async Task Barrier_UnderclaimedEvidence_AffirmedByRepull_StillRenders()
    {
        // The core's documented conservative under-claim: a scan-skipped recompute (a parent
        // recomputed to an unchanged value) keeps an OLDER stamp -- and the same publication
        // sequence -- while its value stays valid. The resulting stamp disagreement is a
        // spurious glitch the re-pull cannot heal (the pull answers with the identical
        // publication, dropped as a duplicate). The barrier must not stay blocked: a re-pull
        // round that changes nothing is both hosts AFFIRMING their current truth, and the
        // affirmed pair renders.
        var host = new MemoFactory();
        var s = host.CreateSignal(4);
        var q = host.CreateMemoizR("q", async () => await s.Get());
        var half = host.CreateMemoizR("half", async () => await s.Get() / 2);
        var c = host.CreateMemoizR("c", async () => await half.Get() * 10);
        using var qExport = host.Export(q, _ => Task.CompletedTask);
        using var cExport = host.Export(c, _ => Task.CompletedTask);

        var consumer = new MemoFactory();
        var qMirror = consumer.CreateRemoteSignal("q", 0, qExport.PullAsync);
        var cMirror = consumer.CreateRemoteSignal("c", 0, cExport.PullAsync);

        var renders = new List<(int Q, int C)>();
        var reaction = DistributedBarrier.CreateConsistentReaction(
            consumer, qMirror, cMirror, (vq, vc) => { lock (renders) { renders.Add((vq, vc)); } });

        await qMirror.PullAsync();
        await cMirror.PullAsync();
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((4, 20)); } });

        // s: 4 -> 5. q recomputes (4 -> 5, fresh stamp); half recomputes to the SAME 2, so c's
        // scan skips the recompute and keeps its old stamp and sequence. Deliver only q's
        // update: the barrier sees the spurious glitch, re-pulls, both hosts affirm, renders.
        await s.Set(5);
        await qMirror.PullAsync();
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((5, 20)); } });

        // The pair moves on with a REAL change (both sides advance consistently), which
        // expires the affirmation ...
        await s.Set(6); // half: 2 -> 3, so c recomputes too
        await qMirror.PullAsync();
        await cMirror.PullAsync();
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((6, 30)); } });

        // ... and a NEW under-claim glitch earns its own heal round and still converges.
        await s.Set(7); // half stays 3: c scan-skips again
        await qMirror.PullAsync();
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((7, 30)); } });
        GC.KeepAlive(reaction);
    }

    [Fact]
    public async Task Barrier_WithAReRacingExport_StillAffirmsAndRenders()
    {
        // A ConcurrentRace is non-memoized: every pull re-races and publishes an equal-content
        // payload under a FRESH sequence. The heal round's "nothing changed" check must
        // compare publication content, not references -- reference identity would refuse the
        // affirmation every round and spin re-pulls forever instead of rendering.
        var host = new MemoFactory();
        var s = host.CreateSignal(4);
        var race = host.CreateConcurrentRace(s.Get, async (_, r) => r);
        var half = host.CreateMemoizR("half", async () => await s.Get() / 2);
        var c = host.CreateMemoizR("c", async () => await half.Get() * 10);
        using var raceExport = host.Export(race, _ => Task.CompletedTask);
        using var cExport = host.Export(c, _ => Task.CompletedTask);

        var consumer = new MemoFactory();
        var raceMirror = consumer.CreateRemoteSignal("race", 0, raceExport.PullAsync);
        var cMirror = consumer.CreateRemoteSignal("c", 0, cExport.PullAsync);

        var renders = new List<(int Race, int C)>();
        var reaction = DistributedBarrier.CreateConsistentReaction(
            consumer, raceMirror, cMirror, (r, v) => { lock (renders) { renders.Add((r, v)); } });

        await raceMirror.PullAsync();
        await cMirror.PullAsync();
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((4, 20)); } });

        // s: 4 -> 5. The race re-evaluates to 5 with a fresh stamp; c's scan-skip keeps its
        // old stamp. Deliver only the race's update: a spurious glitch whose every re-pull of
        // the race answers equal content under a new sequence -- it must still affirm.
        await s.Set(5);
        await raceMirror.PullAsync();
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((5, 20)); } });
        GC.KeepAlive(reaction);
    }

    [Fact]
    public async Task Barrier_ThrowingGlitchCallback_StillHeals()
    {
        // onGlitch is diagnostics only: a throwing sink (a logger that is itself down) must
        // not abort the reaction before the re-pull is scheduled. The failure is reported to
        // onRepullError and the barrier heals regardless.
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
        var reported = new List<Exception>();
        var reaction = DistributedBarrier.CreateConsistentReaction(
            consumer, dewMirror, heatMirror,
            (d, h) => { lock (renders) { renders.Add((d, h)); } },
            onRepullError: ex => { lock (reported) { reported.Add(ex); } },
            onGlitch: (_, _) => throw new InvalidOperationException("glitch sink down"));

        await dewMirror.PullAsync();
        await heatMirror.PullAsync();
        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((15.0, 25.0)); } });

        await temperature.Set(30.0);
        await dewMirror.OnValueAsync(await dewExport.PullAsync());

        await TestHelpers.WaitForConvergenceAsync(() => { lock (renders) { return renders.Contains((25.0, 35.0)); } });
        lock (reported)
        {
            Assert.Contains(reported, ex => ex is InvalidOperationException { Message: "glitch sink down" });
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
