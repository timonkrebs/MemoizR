# ADR 0006 — The actor-engine prototype: GraphActor turns instead of locks

- Status: Accepted (experimental)
- Date: 2026-06-11
- Deciders: MemoizR maintainers
- Issue: [#36 — Strengthen data-race safety guarantees](https://github.com/timonkrebs/MemoizR/issues/36)
- Builds on: [ADR 0003](0003-sendable-checking-and-isolation-assertions.md) (isolation assertions), [ADR 0005](0005-custom-executors.md) (executors)

## Context

Issue #36's literal suggestion is actors. ADR 0003 deferred it: actor-izing the core buys
by-construction safety for internal state the lock-based engine already protects, at the cost of
rewriting the most battle-tested code in the repo. The deferral came with a commitment — a
prototype behind the factory, validated against the same invariants as the shipping engine — and
this ADR records that prototype: what the actor architecture actually looks like in this
codebase, what it removes, what it cannot remove, and what building it revealed about the
shipping engine.

## Decision

### The architecture

One **`GraphActor` per `Context`** (lazy; keyed factories share it): a `System.Threading.Channels`
loop that processes **synchronous turns** one at a time. Turns own ALL graph bookkeeping — state
transitions, generations, dependency capture, link rewiring, invalidation cascades. Turns never
await, and nothing is ever "held" across user code; an evaluation is a transaction of turns:

```
Get ──fast path (2 volatile reads)──────────────────────────► value
 └─► Decide turn  ─ Done / Wait / Scan / Compute
       Compute:  mark Evaluating, snapshot generation, claim evaluation
       (off-actor) run the user computation — nested reads recurse freely
     Commit turn: publish value → rewire links → diamond-mark → generation-checked Clean
```

`GraphActor` implements `IExecutor` (ADR 0005) with exact `IsCurrent` identity, so every
actor-confined mutator carries the layer-3 dynamic check (`Actor.AssertIsolated()`, DEBUG-only):
the confinement claim is *proven on every operation of every Debug test run*, not asserted in
prose. The loop parks on its own channel when idle and holds no external roots, so a dropped
context is collectable, actor and all.

The engine ships as a parallel, deliberately type-separated surface —
`CreateActorSignal` / `CreateActorMemoizR` returning `ActorSignal<T>` / `ActorMemo<T>` — rather
than as a mode of the existing classes. The two engines must not be wired into one graph, and
the types make the mistake uncompilable. Strict Sendable checks apply; the MZR001–003 analyzers
cover the new creations (`ActorSignal.Set` throws inside a computation just like the lock
engine's exclusive-inside-upgradeable rejection, so MZR003 applies — detected flow-side via the
capture frame).

### What the actor removes — and what it cannot

Removed, by construction: every monitor and every piece of cross-monitor reasoning in the
bookkeeping. `Sources`/`Observers` are plain fields mutated in place; the state machine and
generations are plain fields; there is no per-flow `ContextLock`, no per-node mutex (an
actor-confined **waiter queue** provides at-most-one-evaluation: concurrent arrivals park on a
TCS and re-decide when the evaluation ends), no `AsyncLocal` lock-scope reentrancy machinery
(nested reads hold nothing, so there is nothing to re-enter), and no lock-ordering analysis —
concurrency.md §9 has no counterpart here because waiting never holds anything. Cycle detection
falls out of the **evaluation chain** (each frame links to the frame its read arrived under, and
parent scans extend the chain with a link-only frame): a read nested inside the target node's
own in-flight evaluation is a cycle and throws; every other reader waits — including unawaited
sibling reads of one memo issued by the same computation, which share a flow but are not a cycle
(bare flow identity, the first cut, wrongly rejected those). A tracked read of a node from a
*different* context is rejected at the read: capturing a foreign source would make the reader's
commit rewire another actor's observer list. Reads of actor nodes from inside a **lock-engine
computation** are rejected the same way — the read would register in *neither* graph (lock
computations carry no ActorFlow frame; actor nodes implement no lock-engine interface), so the
computation would cache a value no actor-side Set can ever invalidate. The evaluating
computation is found through a context-agnostic flow-ambient marker set by the lock engine's
evaluation paths (a computation of ANY context is the same staleness, so the node's own context
must not be the only one checked); `Untrack` remains the escape hatch for a deliberate snapshot
read. Frames **expire** when their evaluation commits or faults, so deferred work that captured
the flow's `ExecutionContext` inside a computation (`Task.Run` — the documented way to schedule
a write for after the evaluation) reads as outside any evaluation instead of being falsely
rejected by its stale frame. The mirror direction — an actor computation reading a lock-engine
node, the same silent staleness seen from the other side — is guarded at the lock engine's read
entry points, gated on the actor engine being **engaged** at all (a static flag set by the first
actor node): a process that never creates actor nodes pays one predictable branch per read, not
an AsyncLocal probe, which is what kept this direction open until now. A dependency cycle that
only forms at recompute time (detected via the evaluation chain) surfaces as
`CyclicDependencyException` even when it closes through a parent *scan* — a scan converts
ordinary parent faults into "stay CacheCheck and retry", but a cycle is a structural error that
must not hide behind a stale serve. Independent computations still run fully in parallel — the
actor serializes *turns*, never user code (pinned by test).

Kept, deliberately:

- **The generation guard.** An evaluation spans multiple turns, so an invalidation can land
  between Begin and Commit; the optimistic commit is inherent to lazy memoization, not to the
  locking choice — exactly as predicted when this layer was scoped.
- **The lock-free fast path.** Two volatile fields (the state enum and the value box, written
  box-before-state in the commit turn) with the identical ADR-0001 rule-4 release/acquire
  argument. These two fields are the actor engine's *entire* memory-model surface.

### The finding: late-wired observers, and the read-evidence guard

Porting the suite's invariants included an **un-primed** chain storm (writers and readers racing
the graph's *first* evaluations — every existing stress test primes its graph first). The actor
engine failed it reproducibly, and the diagnostic dump pinned a protocol hole that is not
actor-specific:

> Observer links wire at **commit**. A cascade that dirties a source **suppresses** propagation
> when the source was already dirty, on the assumption that "observers were already notified" —
> an assumption violated by any observer that *arrives after* the source went dirty. The
> late-wired observer commits Clean over a dirty parent, and no future cascade can ever reach it:
> permanently stale, served forever by the (correct) Clean fast path.

The same un-primed storm reproduced the same permanent staleness in the **lock-based engine** —
the hole was latent there too, masked only by the priming patterns of the existing tests. It is
**now closed in both engines**, by two equivalent mechanisms:

- **Lock engine — eager subscription.** `Context.CheckDependenciesTheSame` subscribes the reader
  to a source at *capture time*, before the source recomputes, instead of only at the end of the
  evaluation. An in-flight `Set` then reaches the reader mid-evaluation and bumps its generation,
  so the existing generation guard refuses the stale commit. (Verified causally:
  `RegressionTests.LockEngine_UnprimedChainUnderStorm_NeverStrandsStale` passes with the eager
  subscription and fails within a round or two when it is reverted.)
- **Actor engine — read evidence** (below).

Both reach the same outcome — a node cannot commit Clean over a source that was invalidated
between the read and the commit — from opposite directions: the lock engine guarantees the
*notification* arrives (the link is in place early), the actor engine detects the *miss* from the
read's recorded generation without depending on a link at all.

The actor engine closes the hole with **read evidence**: a tracked read records
`(source, source.Generation)` *in the same turn that serves the value* (any earlier or later and
a concurrent invalidation slips between value and evidence). A commit may go Clean only if its
own generation snapshot is intact AND every captured pair is still current. Every non-Clean
outcome bumps the node's generation *after* recording, so in-flight consumers of an unconfirmed
value are guaranteed a mismatch and park themselves Dirty in turn — staleness can no longer hide
behind a missing link, it is detected from the consumer's side. The *after recording* order is
load-bearing on the failure paths too: a faulted computation, a faulted commit, and a scan that
could not verify a faulted parent all record the caller's pair first and then bump, so a caller
that catches the failure and returns a fallback parks Dirty instead of committing the fallback
Clean over a Dirty parent whose later recovery write would be suppressed. (A pleasant corollary:
a computation that reads the same source twice across an intervening change is also caught — the
two pairs cannot both match.) Signals bump their generation only on value-*changing* Sets;
equal-value writes are complete no-ops — nothing derived can have become stale, and notifying
anyway bumped observer generations and refused their still-valid in-flight commits (the lock
engine's `Signal.Set` follows the same rule). The regression test is
`ActorEngineTests.UnprimedChainUnderStorm_NeverStrandsStale`.

### Deliberate divergences from the lock engine

- **Dirty-on-throw.** A failed computation parks the memo Dirty (always retried on the next
  Get). The lock engine parks at CacheCheck, which can strand a *first-run* failure (no source
  links exist to re-dirty it); that edge is untested there.
- **No early break in the parent scan**, no prefix-optimized rewiring (full re-wire per commit),
  no `CancellationTokenSource` parameter, no labels, no `Untrack` — prototype scope.

### Scope and non-goals (v1)

Signals and memos only. Reactions, the structured-concurrency nodes, and
`EagerRelativeSignal` remain lock-engine-only; reactions on the actor (debounce scheduling as
turns, effects on a layer-4 executor) are the natural next increment. Coyote exploration of the
turn loop is follow-up — the deterministic I2/I3 ports and the storm tests are the current
systematic evidence.

## Consequences

Positive:

- The issue's thesis is now demonstrated in-repo: actor isolation plus structured turn
  transactions yields the same observable semantics with a fraction of the synchronization
  surface (two volatile fields versus five lock layers), no deadlock analysis, and dynamic
  isolation checks on every mutation.
- The prototype already paid for itself: it found and reproduced a latent permanent-staleness
  bug in the shipping lock engine that the existing (always-primed) stress tests had missed. That
  hole is now closed in both engines — eager capture-time subscription in the lock engine,
  read-evidence in the actor engine — and guarded by an un-primed storm test on each.

Costs / accepted trade-offs:

- Every bookkeeping step pays a channel hop and a TCS allocation; read evidence adds per-read
  tuples. Unmeasured (benchmarks are follow-up); the lock engine remains the default.
- Two engines to maintain until parity or convergence is decided.

## Alternatives considered

- **Rewriting the lock engine in place behind a mode flag.** Rejected: every method would branch
  on the mode, destabilizing the battle-tested path while delivering the actor's simplification
  to neither.
- **A lock-shaped actor** (implementing the `ContextLock` interface over the actor). Rejected:
  a lock is *held across awaits* — the one thing an actor must never do; the value of the actor
  model is the Begin/Commit turn restructure, which no lock interface can express.
- **Per-node actors.** Rejected for the core: an invalidation cascade must be atomic across the
  affected subgraph (one turn), which per-node actors would turn into a distributed transaction.
  Per-context is the natural isolation domain — it is already the graph-sharing boundary.
