# MemoizR.Distributed

Glitch-free synchronization of MemoizR reactive graphs across process and machine boundaries,
built on the Causality Trigger Clock (space-efficient ITC-style causality stamps that travel
with every value).

- **`Export`** a node on the host: value changes push a tiny stale advertisement; consumers
  pull lazily, so MemoizR's laziness survives the network.
- **`CreateRemoteSignal`** on the consumer: a local read-only signal fed by an adoption
  protocol that makes reordered, at-least-once transports harmless — per-publication sequences
  totally order deliveries, incarnation epochs detect host restarts (committed only through
  the mirror's own pull; held evidence is discarded, never merged), abandoned epochs drop late
  traffic from dead incarnations, and misrouted payloads for a different node are rejected.
- **`DistributedBarrier`** combines mirrored inputs only on a consistent, verified snapshot of
  the host's write history — a transient glitch (one mirror fresh, one stale, straddling the
  same write) is detected via the causality stamps and healed by re-pulling the lagging side,
  and a disagreement the hosts themselves affirm on re-pull (the core's stamps are
  deliberately conservative) renders instead of blocking.

The transport is yours: three messages (stale / pull / value), delegate-shaped, with an
in-process pairing being a few lines of glue. See the repository's
`samples/DistributedGraphSample` and `docs/architecture/causality-trigger-clock.md`.
