# Distributed reactive graph sample

A minimal two-peer bridge built on the **MemoizR.Distributed** package
(tracking: [issue #148](https://github.com/timonkrebs/MemoizR/issues/148), design:
[docs/architecture/causality-trigger-clock.md](../../docs/architecture/causality-trigger-clock.md)).

Peer A hosts the source-of-truth graph; peer B mirrors two of A's derived values and combines
them **glitch-free — with no lock spanning the peers** — via the causality stamps that travel
with every value:

```
peer A                                    peer B
──────                                    ──────
temperature ──┬─► dewPoint  ──[wire]──►   dewMirror  ──┐
              │                                        ├─► comfort (glitch barrier)
humidity ─────┴─► heatIndex ──[wire]──►   heatMirror ──┘
```

The whole wire protocol is three messages (`Stale`, `Pull`, `Value`); values move only on
pull, so MemoizR's laziness survives the network. The script plays the transport by hand —
delivering, delaying and duplicating messages deliberately — so every mechanism is visible:

1. **Initial sync** — B pulls both nodes (`RemoteSignal`), the barrier renders on a consistent
   snapshot.
2. **A racing update** — temperature changes on A; only dewPoint's advertisement is delivered.
   The barrier detects that the mirrors' evidence disagrees on temperature's trigger,
   re-pulls the lagging mirror **itself**, and renders once consistent.
3. **A late duplicate delivery** — dropped by the per-publication *sequence* order (which also
   settles the orderings causality stamps deliberately cannot: a dependency set oscillating
   through empty re-publishes an earlier stamp on a newer value); reordered and at-least-once
   transports are harmless.
4. **A peer restart** — the fresh incarnation epoch is detected on the first payload; held
   evidence is discarded (never merged) and the `OnPeerReset` resubscription hook runs.
5. **Late traffic from the pre-reset incarnation** — dropped: epochs are random identifiers,
   not ordered, so the mirror remembers the epochs it abandoned instead of trusting a
   mismatch to mean "newer".

Run it:

```
dotnet run --project samples/DistributedGraphSample
```

What the package deliberately does **not** do yet: splice peer A's stamps into peer B's
*local* stamps. The foreign evidence travels beside B's graph (in the `RemoteSignal`) and the
barrier checks it explicitly — first-class splicing needs the multi-peer epoch table
(wire-format v3), tracked in [#148](https://github.com/timonkrebs/MemoizR/issues/148).
