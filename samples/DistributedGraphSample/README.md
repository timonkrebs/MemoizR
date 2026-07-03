# Distributed reactive graph sample

A minimal two-peer bridge over the **Causality Trigger Clock**
([issue #39](https://github.com/timonkrebs/MemoizR/issues/39), design:
[docs/architecture/causality-trigger-clock.md](../../docs/architecture/causality-trigger-clock.md)).

Peer A hosts the source-of-truth graph; peer B mirrors two of A's derived values and combines
them **glitch-free — with no lock spanning the peers** — by checking the causality stamps that
travel with every value:

```
peer A                                    peer B
──────                                    ──────
temperature ──┬─► dewPoint  ──[wire]──►   dewMirror  ──┐
              │                                        ├─► comfort (glitch barrier)
humidity ─────┴─► heatIndex ──[wire]──►   heatMirror ──┘
```

The whole wire protocol is three messages (`Stale`, `Pull`, `Value`); values move only on
pull, so MemoizR's laziness survives the network. The sample drives the message flow by hand
so every mechanism is visible:

1. **Initial sync** — B pulls both nodes, the barrier renders on a consistent snapshot.
2. **A racing update** — temperature changes on A; only dewPoint's update is delivered. The
   barrier detects that the inputs disagree on temperature's trigger (`IsConsistentWith`),
   identifies the lagging input (`IsDominatedBy`), has it re-pulled, and renders once
   consistent.
3. **A late duplicate delivery** — dropped because its stamp is dominated by the held
   evidence; reordered and at-least-once transports are harmless.
4. **A peer restart** — the fresh incarnation epoch is detected on the first payload; held
   evidence is discarded, never merged.
5. **Late traffic from the pre-reset incarnation** — dropped: epochs are random identifiers,
   not ordered, so the mirror remembers the epochs it abandoned instead of trusting a
   mismatch to mean "newer".

Run it:

```
dotnet run --project samples/DistributedGraphSample
```

What this sample deliberately does **not** do: splice peer A's stamps into peer B's *local*
stamps. The foreign evidence travels beside B's graph (in the `Mirror`), and the barrier
checks it explicitly — a stamp-adopting `RemoteSignal<T>` that publishes a foreign
`(value, evidence)` pair into the local evidence chain is the future `MemoizR.Distributed`
package's job (a friend assembly, like `MemoizR.Reactive`), together with the multi-peer
epoch table (wire format v3).
