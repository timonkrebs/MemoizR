using MemoizR;
using MemoizR.Reactive;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Distributed reactive graph sample (issue #39, docs/architecture/causality-trigger-clock.md)
//
// Peer A hosts the source-of-truth graph; peer B mirrors two of A's derived values and
// combines them GLITCH-FREE -- without any lock spanning the two peers -- by checking the
// causality stamps that travel with every value:
//
//   peer A                                    peer B
//   ──────                                    ──────
//   temperature ──┬─► dewPoint  ──[wire]──►   dewMirror  ──┐
//                 │                                        ├─► comfort (glitch barrier)
//   humidity ─────┴─► heatIndex ──[wire]──►   heatMirror ──┘
//
// Both mirrored inputs derive from the SAME signals, so when temperature changes on A and the
// two updates race across the wire, B can transiently hold a fresh dewPoint next to a stale
// heatIndex. The barrier detects that (their stamps disagree on temperature's trigger), has
// the lagging input re-pulled, and only renders on a consistent snapshot.
//
// The whole protocol is three messages; values move only on Pull, so laziness survives the
// network. This sample drives the message flow by hand to make every step visible -- a real
// bridge would pump the same handlers from a gRPC stream or WebSocket.
// ═══════════════════════════════════════════════════════════════════════════════════════════

// ───────────────────────── peer A: the source-of-truth graph ─────────────────────────
// Disjoint node-id slices per peer: stamps merged across peers can never collide on an id,
// and each peer occupies its own subtree of the stamp's interval encoding.
var peerA = new MemoFactory("sample-peer-a", idRangeStart: 1_000, idRangeEnd: 2_000);

var temperature = peerA.CreateSignal("temperature", 24.0);
var humidity = peerA.CreateSignal("humidity", 0.40);
var dewPoint = peerA.CreateMemoizR("dewPoint", async () =>
    await temperature.Get() - (1 - await humidity.Get()) * 5);
var heatIndex = peerA.CreateMemoizR("heatIndex", async () =>
    await temperature.Get() + 0.1 * await humidity.Get() * (await temperature.Get() - 14.5));

// An EXPORT is just a reaction: whenever the node's VALUE advances, push a stale notification
// (id + stamp, no value). This sample parks them in per-node outboxes so the "network" can
// deliver them out of order below; a real bridge would write to its transport here.
// Deliberate design boundary: the local invalidation cascade propagates only value changes, so
// an evaluation that keeps the value but changes the EVIDENCE (a stamp refresh, or an
// unverifiable spell healing) emits no notification -- evidence-only transitions are
// discovered by PULLING (the glitch barrier re-pulls lagging inputs, and an unverifiable
// mirror chases every advertisement); a real bridge adds a heartbeat/poll for liveness.
var outbox = new Outbox(dewPoint.Id, heatIndex.Id);
var exportDew = peerA.BuildReaction("export dewPoint").CreateReaction(dewPoint, _ =>
    outbox.Enqueue(new StaleMsg(dewPoint.Id, dewPoint.Stamp.Serialize())));
var exportHeat = peerA.BuildReaction("export heatIndex").CreateReaction(heatIndex, _ =>
    outbox.Enqueue(new StaleMsg(heatIndex.Id, heatIndex.Stamp.Serialize())));

// ───────────────────────── peer B: mirrors and the glitch barrier ─────────────────────────
var peerB = new MemoFactory("sample-peer-b", idRangeStart: 2_000, idRangeEnd: 3_000);

// Each mirror carries the consumer side of the wire protocol; its Pull delegate is the host's
// endpoint for that node: recompute lazily, answer with the untorn (value, evidence) pair.
var dewMirror = new Mirror("dewPoint", peerB.CreateEagerRelativeSignal("dewMirror", 0.0), async () =>
{
    var (value, evidence) = await dewPoint.GetWithEvidence();
    return new ValueMsg(dewPoint.Id, value, evidence.Stamp.Serialize(), evidence.Unverifiable);
});
var heatMirror = new Mirror("heatIndex", peerB.CreateEagerRelativeSignal("heatMirror", 0.0), async () =>
{
    var (value, evidence) = await heatIndex.GetWithEvidence();
    return new ValueMsg(heatIndex.Id, value, evidence.Stamp.Serialize(), evidence.Unverifiable);
});

// THE GLITCH BARRIER: combine the two mirrored inputs only on a consistent snapshot of peer
// A's write history. Both stamps cover temperature and humidity; IsConsistentWith fails
// exactly when the inputs straddle a write, and IsDominatedBy tells which side lags. The
// barrier only REPORTS the lagging input -- the re-pull runs on the bridge's own flow (below,
// the demo script plays that part), because a reaction body must not Set signals of its own
// graph from inside its own evaluation.
var rendered = new TaskCompletionSource<(double Dew, double Heat)>(TaskCreationOptions.RunContinuationsAsynchronously);
var glitched = new TaskCompletionSource<Mirror>(TaskCreationOptions.RunContinuationsAsynchronously);
var comfort = peerB.BuildReaction("comfort").CreateReaction(
    dewMirror.Local, heatMirror.Local,
    (dew, heat) =>
    {
        // Absent evidence (nothing synced yet) and unverifiable evidence mean the same thing
        // to a consumer: CANNOT VERIFY -- so no render, never a guess. Gated on HasEvidence,
        // not on the stamp being non-empty: an honestly-empty stamp (a value depending on no
        // tracked signals) is verifiable evidence and consistent with anything.
        if (!dewMirror.HasEvidence || !heatMirror.HasEvidence
            || dewMirror.Unverifiable || heatMirror.Unverifiable)
        {
            return;
        }

        if (!dewMirror.Remote.IsConsistentWith(heatMirror.Remote))
        {
            var lagging = dewMirror.Remote.IsDominatedBy(heatMirror.Remote) ? dewMirror : heatMirror;
            Console.WriteLine($"   [B] GLITCH detected (inputs disagree on a shared signal) -> {lagging.Name} lags");
            glitched.TrySetResult(lagging); // hand the re-pull to the bridge's pump
            return;
        }

        Console.WriteLine($"   [B] render comfort(dewPoint: {dew:F2}, heatIndex: {heat:F2})  <- consistent snapshot");
        rendered.TrySetResult((dew, heat));
    });

// ───────────────────────── the demo script ─────────────────────────
Console.WriteLine("1) initial sync: B pulls both exported nodes");
await dewMirror.PullAsync();
await heatMirror.PullAsync();
await rendered.Task;

Console.WriteLine("\n2) temperature changes on A; the two stale notifications race -- deliver only dewPoint's");
rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
var staleDewPayload = await dewMirror.Pull(); // keep a pre-change payload for step 3
// The export reactions each run once EAGERLY on creation; wait for those initial
// notifications to land before discarding them. Draining first would race the initial runs:
// one landing after the drain (but before the Set) would leave a pre-change stamp as the only
// dewPoint message, the dominance check in OnStaleAsync would skip the pull, and the glitch
// this step demonstrates would never materialize -- the script would hang on glitched.Task.
await outbox.WaitForAllAsync();
outbox.Drain(); // step 2 cares about the post-change notifications only
await temperature.Set(30.0);
await outbox.WaitForAllAsync(); // let A's (debounced) export reactions run
await dewMirror.OnStaleAsync(outbox.DequeueLatest(dewPoint.Id));
// heatIndex's stale is still "in flight": B now holds fresh dewPoint next to stale heatIndex.
// The barrier reports the lagging input; the pump re-pulls it; the barrier renders once the
// snapshot is consistent:
await (await glitched.Task).PullAsync();
var (dewRendered, heatRendered) = await rendered.Task;
Console.WriteLine($"   healed: dew={dewRendered:F2}, heat={heatRendered:F2}");

Console.WriteLine("\n3) a late duplicate of the OLD dewPoint payload arrives -- dominance drops it");
await dewMirror.OnValueAsync(staleDewPayload);

Console.WriteLine("\n4) peer A restarts: same ids and triggers, but a fresh incarnation epoch");
var restartedA = new MemoFactory(null, idRangeStart: 1_000, idRangeEnd: 2_000); // unkeyed: a new context
var temperature2 = restartedA.CreateSignal("temperature", 18.0);
var humidity2 = restartedA.CreateSignal("humidity", 0.55);
var dewPoint2 = restartedA.CreateMemoizR("dewPoint", async () =>
    await temperature2.Get() - (1 - await humidity2.Get()) * 5);
var (value2, evidence2) = await dewPoint2.GetWithEvidence();
await dewMirror.OnValueAsync(new ValueMsg(dewPoint2.Id, value2, evidence2.Stamp.Serialize(), evidence2.Unverifiable));

Console.WriteLine("\n5) a late payload from the PRE-RESET incarnation arrives -- its abandoned epoch drops it");
await dewMirror.OnValueAsync(staleDewPayload);

Console.WriteLine("\ndone.");
GC.KeepAlive(exportDew);
GC.KeepAlive(exportHeat);
GC.KeepAlive(comfort);

// ───────────────────────── the wire protocol: three tiny messages ─────────────────────────
// Stamps travel in the frozen v2 binary format; Deserialize validates hostile input.
record StaleMsg(int NodeId, byte[] Stamp);
record ValueMsg(int NodeId, double Value, byte[] Stamp, bool Unverifiable);

// A mirror pairs a LOCAL signal (so B's graph reacts through the ordinary machinery) with the
// FOREIGN evidence the current value arrived with (so cross-peer consistency stays checkable)
// and the consumer side of the wire protocol. A future MemoizR.Distributed package folds these
// into one stamp-adopting RemoteSignal<T>; until then the evidence travels beside the graph.
sealed class Mirror(string name, EagerRelativeSignal<double> local, Func<Task<ValueMsg>> pull)
{
    public string Name { get; } = name;
    // An EAGER relative signal on purpose: a re-pull can change the EVIDENCE while the numeric
    // value stays identical (e.g. the lagging side of a glitch recomputes to the same number).
    // A plain Signal.Set would suppress the notification and the barrier would never re-run to
    // observe the fresh evidence; the eager signal always notifies.
    public EagerRelativeSignal<double> Local { get; } = local;
    public Func<Task<ValueMsg>> Pull { get; } = pull;
    public CausalityStamp Remote { get; private set; } = CausalityStamp.Empty;
    // ORDERING FLOOR: the join of every non-empty stamp adopted in the current epoch. Late and
    // duplicate deliveries are ordered against this, not against Remote: adopting an honestly-
    // empty publication rightly clears the CURRENT evidence (an empty stamp claims nothing),
    // but it must not amnesty older non-empty payloads still in flight on a reordered
    // transport.
    private CausalityStamp floor = CausalityStamp.Empty;
    // Epochs this mirror has moved on from. Epochs are random identifiers, not ordered: a
    // mismatch alone cannot tell "the peer restarted" from "a payload of the incarnation we
    // already left arrived late", so the mirror remembers what it abandoned. (A real bridge
    // bounds this set by resubscribing on reset.)
    private readonly HashSet<long> abandonedEpochs = [];
    public volatile bool Unverifiable;
    // Whether any payload was adopted at all: an honestly-empty stamp (a value that depends on
    // no tracked signals) is real, verifiable evidence, so "synced" cannot be derived from the
    // stamp being non-empty.
    public volatile bool HasEvidence;

    public async Task PullAsync() => await OnValueAsync(await Pull());

    // STALE handler: pull only when the host advertises something newer than we hold -- the
    // value itself moves lazily. Two exceptions always pull: an EMPTY advertisement (the host
    // published no claim, e.g. an unverifiable evaluation) carries no ordering information,
    // and a mirror that is currently UNVERIFIABLE must chase every advertisement -- a host
    // recovering without any trigger advancing re-advertises the very stamp we already hold,
    // which dominance would otherwise mistake for old news.
    public async Task OnStaleAsync(StaleMsg msg)
    {
        var advertised = CausalityStamp.Deserialize(msg.Stamp);
        if (Unverifiable || advertised.Epoch == 0 || !advertised.IsDominatedBy(Remote))
        {
            await PullAsync();
        }
    }

    public async Task OnValueAsync(ValueMsg msg)
    {
        var incoming = CausalityStamp.Deserialize(msg.Stamp);

        // An UNVERIFIABLE payload carries the empty stamp, so it must be adopted BEFORE the
        // ordering checks below (an empty stamp is dominated by anything): the consumer has to
        // stop trusting its held evidence now, not keep rendering stale verified state. The
        // held stamps are kept -- they still order future verified payloads -- but the
        // dominance drop below relaxes to strictly-older-only while unverifiable, so a recovery
        // republishing the SAME stamp (a transient fault healing without any trigger advancing)
        // is not mistaken for a duplicate.
        if (msg.Unverifiable)
        {
            Console.WriteLine($"   [B] {Name}: host published UNVERIFIABLE evidence -> rendering blocked until verified again");
            Unverifiable = true;
            HasEvidence = true;
            await Local.Set(_ => msg.Value);
            return;
        }

        // LATE TRAFFIC FROM A DEAD INCARNATION: a payload stamped with an epoch this mirror
        // already abandoned is stale by definition. Without this check the reset detection
        // below would fire AGAIN on the mismatch (epochs carry no order -- "different" cannot
        // mean "newer") and roll the mirror back to the dead incarnation's state.
        if (incoming.Epoch != 0 && abandonedEpochs.Contains(incoming.Epoch))
        {
            Console.WriteLine($"   [B] {Name}: DROPPED delivery (stamp from an abandoned incarnation)");
            return;
        }

        // RESET detection: a different incarnation epoch means the host restarted -- its ids
        // and triggers started over, so held evidence must be discarded, never merged (Join
        // across epochs throws by design). Keyed off the ordering floor, not Remote: the floor
        // still carries the old epoch while Remote is honestly empty. A real bridge would
        // resubscribe here.
        if (floor.Epoch != 0 && incoming.Epoch != 0 && incoming.Epoch != floor.Epoch)
        {
            Console.WriteLine($"   [B] {Name}: peer RESET detected (epoch changed) -> discarding held evidence");
            abandonedEpochs.Add(floor.Epoch);
            Remote = CausalityStamp.Empty;
            floor = CausalityStamp.Empty;
        }

        // LATE / DUPLICATE delivery, ordered against the FLOOR: ordering information only
        // exists between two NON-EMPTY stamps -- an honestly-empty stamp (the exported node
        // legitimately depends on no tracked signals) is a real publication, not old news.
        // While VERIFIED, anything dominated (including equal re-deliveries) is dropped, which
        // is what makes at-least-once transports harmless. While UNVERIFIABLE, only payloads
        // STRICTLY below the floor are dropped: a floor-equal verified payload is exactly the
        // recovery (a transient fault healed without any trigger advancing) and must be
        // adopted -- but a genuinely older late delivery must still not resurrect stale
        // verified state.
        if (floor.Epoch != 0 && incoming.Epoch != 0 && incoming.IsDominatedBy(floor)
            && (!Unverifiable || !floor.IsDominatedBy(incoming)))
        {
            Console.WriteLine($"   [B] {Name}: DROPPED delivery (stamp dominated by held evidence)");
            return;
        }

        Remote = incoming;
        floor = floor.Join(incoming); // empty joins as identity; same epoch is guaranteed above
        Unverifiable = false;
        HasEvidence = true;
        await Local.Set(_ => msg.Value); // from here, B's ordinary local reactivity takes over
    }
}

// The host-side outboxes standing in for a transport, one queue per exported node, so the
// demo can deliver notifications deliberately out of order.
sealed class Outbox(params int[] nodeIds)
{
    private readonly Dictionary<int, Queue<StaleMsg>> queues = nodeIds.ToDictionary(id => id, _ => new Queue<StaleMsg>());

    public void Enqueue(StaleMsg msg)
    {
        lock (queues)
        {
            queues[msg.NodeId].Enqueue(msg);
        }
    }

    public void Drain()
    {
        lock (queues)
        {
            foreach (var queue in queues.Values)
            {
                queue.Clear();
            }
        }
    }

    public StaleMsg DequeueLatest(int nodeId)
    {
        lock (queues)
        {
            var queue = queues[nodeId];
            var last = queue.Dequeue();
            while (queue.Count > 0)
            {
                last = queue.Dequeue();
            }
            return last;
        }
    }

    // The export reactions are debounced; poll until every outbox carries a notification.
    public async Task WaitForAllAsync()
    {
        for (var i = 0; i < 500; i++)
        {
            lock (queues)
            {
                if (queues.Values.All(queue => queue.Count > 0))
                {
                    return;
                }
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("exports did not fire");
    }
}
