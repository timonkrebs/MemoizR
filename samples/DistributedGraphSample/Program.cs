using MemoizR;
using MemoizR.Distributed;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Distributed reactive graph sample, built on the MemoizR.Distributed package
// (issue #148; design: docs/architecture/causality-trigger-clock.md).
//
// Peer A hosts the source-of-truth graph; peer B mirrors two of A's derived values and
// combines them GLITCH-FREE -- without any lock spanning the two peers -- via the causality
// stamps that travel with every value:
//
//   peer A                                    peer B
//   ──────                                    ──────
//   temperature ──┬─► dewPoint  ──[wire]──►   dewMirror  ──┐
//                 │                                        ├─► comfort (glitch barrier)
//   humidity ─────┴─► heatIndex ──[wire]──►   heatMirror ──┘
//
// Both mirrored inputs derive from the SAME signals, so when temperature changes on A and the
// two updates race across the wire, B can transiently hold a fresh dewPoint next to a stale
// heatIndex. The barrier detects that (their stamps disagree on temperature's trigger),
// re-pulls the lagging mirror itself, and renders only on a consistent snapshot.
//
// The wire protocol is three messages (stale / pull / value); values move only on pull, so
// laziness survives the network. This script plays the transport by hand -- delivering,
// delaying and duplicating messages deliberately -- to make every mechanism visible; a real
// bridge pumps the same three handlers from a gRPC stream or WebSocket.
// ═══════════════════════════════════════════════════════════════════════════════════════════

// ───────────────────────── peer A: the source-of-truth graph ─────────────────────────
// Disjoint node-id slices per peer: stamps merged across peers can never collide on an id.
var peerA = new MemoFactory("sample-peer-a", idRangeStart: 1_000, idRangeEnd: 2_000);

var temperature = peerA.CreateSignal("temperature", 24.0);
var humidity = peerA.CreateSignal("humidity", 0.40);
var dewPoint = peerA.CreateMemoizR("dewPoint", async () =>
    await temperature.Get() - (1 - await humidity.Get()) * 5);
var heatIndex = peerA.CreateMemoizR("heatIndex", async () =>
    await temperature.Get() + 0.1 * await humidity.Get() * (await temperature.Get() - 14.5));

// EXPORTS: whenever a node's value advances, the package pushes a stale advertisement (id +
// ordering header + stamp, no value) to this sink -- the demo's stand-in for a transport. The
// sink keeps only the LATEST advertisement per node, exactly like a coalescing send buffer.
var wire = new AdvertisementBuffer();
using var dewExport = peerA.Export(dewPoint, wire.PublishAsync);
using var heatExport = peerA.Export(heatIndex, wire.PublishAsync);

// ───────────────────────── peer B: mirrors and the glitch barrier ─────────────────────────
var peerB = new MemoFactory("sample-peer-b", idRangeStart: 2_000, idRangeEnd: 3_000);

// Each mirror's pull delegate is the host's answer path for that node: recompute lazily,
// answer with the untorn (value, evidence, sequence) triple of one publication.
var dewMirror = peerB.CreateRemoteSignal("dewMirror", 0.0, dewExport.PullAsync,
    onPeerReset: () => { Console.WriteLine("   [B] dewMirror: peer RESET detected (epoch changed) -> discarding evidence, resubscribing"); return Task.CompletedTask; });
var heatMirror = peerB.CreateRemoteSignal("heatMirror", 0.0, heatExport.PullAsync);

// THE GLITCH BARRIER: renders only on consistent, verified snapshots of peer A's write
// history, and re-pulls the lagging mirror itself when the evidence disagrees.
var renders = new RenderLog();
var comfort = DistributedBarrier.CreateConsistentReaction(
    peerB, dewMirror, heatMirror,
    (dew, heat) =>
    {
        Console.WriteLine($"   [B] render comfort(dewPoint: {dew:F2}, heatIndex: {heat:F2})  <- consistent snapshot");
        renders.Add(dew, heat);
    },
    onGlitch: (_, _) => Console.WriteLine("   [B] GLITCH detected (inputs disagree on a shared signal) -> re-pulling the lagging mirror"));

// ───────────────────────── the demo script ─────────────────────────
Console.WriteLine("1) initial sync: B pulls both exported nodes");
await dewMirror.PullAsync();
await heatMirror.PullAsync();
await renders.WaitFor(21.00, 24.38);

Console.WriteLine("\n2) temperature changes on A; the two stale advertisements race -- deliver only dewPoint's");
var staleDewPayload = await dewExport.PullAsync(); // keep a pre-change payload for step 3
var dewSeqBefore = staleDewPayload.Sequence;
await temperature.Set(30.0);
// The publication SEQUENCE makes "wait until the post-change advertisement exists"
// deterministic -- no draining races: newer publication, strictly higher sequence.
var freshDewAdvert = await wire.WaitForAdvertisementAfter(dewPoint.Id, dewSeqBefore);
await dewMirror.OnStaleAsync(freshDewAdvert);
// heatIndex's advertisement is still "in flight": B now holds fresh dewPoint next to stale
// heatIndex. The barrier detects the glitch, re-pulls heatMirror itself, and renders once
// the snapshot is consistent:
await renders.WaitFor(27.00, 30.62);

Console.WriteLine("\n3) a late duplicate of the OLD dewPoint payload arrives -- the sequence order drops it");
await dewMirror.OnValueAsync(staleDewPayload);
Console.WriteLine($"   [B] dewMirror still {await dewMirror.Local.Get():F2} (late delivery dropped)");

Console.WriteLine("\n4) peer A restarts: same ids and triggers, but a fresh incarnation epoch");
// A restart invalidates EVERY mirror of that peer: a real bridge resubscribes them all
// together (the multi-peer epoch table planned as wire-format v3 makes that first-class).
// This demo migrates only dewMirror, so tear the barrier down first -- it would otherwise
// correctly chase the half-migrated pair forever.
comfort.Dispose();
var restartedA = new MemoFactory(null, idRangeStart: 1_000, idRangeEnd: 2_000); // unkeyed: a new context
var temperature2 = restartedA.CreateSignal("temperature", 18.0);
var humidity2 = restartedA.CreateSignal("humidity", 0.55);
var dewPoint2 = restartedA.CreateMemoizR("dewPoint", async () =>
    await temperature2.Get() - (1 - await humidity2.Get()) * 5);
using var dewExport2 = restartedA.Export(dewPoint2, wire.PublishAsync);
await dewMirror.OnValueAsync(await dewExport2.PullAsync());
Console.WriteLine($"   [B] dewMirror adopted the new incarnation's value: {await dewMirror.Local.Get():F2}");

Console.WriteLine("\n5) a late payload from the PRE-RESET incarnation arrives -- its abandoned epoch drops it");
await dewMirror.OnValueAsync(staleDewPayload);
Console.WriteLine($"   [B] dewMirror still {await dewMirror.Local.Get():F2} (dead-incarnation delivery dropped)");

Console.WriteLine("\ndone.");
GC.KeepAlive(comfort);

// ───────────────────────── demo plumbing ─────────────────────────

// The stand-in transport: keeps the latest advertisement per exported node (a coalescing send
// buffer), and lets the script wait for the advertisement of a publication NEWER than a known
// sequence -- deterministic because sequences strictly increase per publication.
sealed class AdvertisementBuffer
{
    private readonly Dictionary<int, StaleNotification> latest = new();

    public Task PublishAsync(StaleNotification advertisement)
    {
        lock (latest)
        {
            latest[advertisement.NodeId] = advertisement;
        }
        return Task.CompletedTask;
    }

    public async Task<StaleNotification> WaitForAdvertisementAfter(int nodeId, long sequence)
    {
        for (var i = 0; i < 500; i++)
        {
            lock (latest)
            {
                if (latest.TryGetValue(nodeId, out var advertisement) && advertisement.Sequence > sequence)
                {
                    return advertisement;
                }
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("the export did not advertise");
    }
}

// Collects rendered snapshots so the script can await a specific one (the debounced barrier
// may legitimately render the same consistent snapshot more than once).
sealed class RenderLog
{
    private readonly List<(double Dew, double Heat)> rendered = new();

    public void Add(double dew, double heat)
    {
        lock (rendered)
        {
            rendered.Add((Math.Round(dew, 2), Math.Round(heat, 2)));
        }
    }

    public async Task WaitFor(double dew, double heat)
    {
        for (var i = 0; i < 500; i++)
        {
            lock (rendered)
            {
                if (rendered.Contains((dew, heat)))
                {
                    return;
                }
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"the barrier never rendered ({dew:F2}, {heat:F2})");
    }
}
