# ADR 0001 — Use of `volatile` fields in MemoizR

- Status: Accepted
- Date: 2026-06-06
- Updated: 2026-06-06 — the lock-free `Value` read is now made tear-free and consistent
  (rule 4); it is no longer an accepted "eventually consistent" limitation.
- Updated: 2026-06-06 — corrected the `ContextLock` scope description (it is per-flow, not
  per-`Context`) and documented the generation guard that closes the cross-flow lost-update
  race it leaves open (see "Cross-flow state coordination" below).
- Updated: 2026-06-10 — hardened the generation guard (unconditional bump on `Invalidate`,
  observer re-notification on a refused commit) and moved `CurrentGets`/`CurrentGetsIndex`
  back to rule 2: `StructuredReduceJob`'s parallel children share their parent flow's scope, so
  no single monitor covers all their accesses (review findings, PR follow-up).
- Deciders: MemoizR maintainers

## Context

MemoizR is a concurrent reactive graph. Its thread-safety rests almost entirely on **locks**,
not on lock-free field access:

- The `ContextLock` (an `AsyncAsymmetricLock`, upgradeable for reads, exclusive for writes) lives
  on the per-flow `ReactionScope`, so it serializes graph evaluation **within a flow**, not across
  independent flows. This is deliberate: structured-concurrency children call
  `Context.ForceNewScope()` and run on their own scope, so a single shared lock would let a parent
  holding it deadlock against the children it is awaiting. The cross-flow gap this leaves on the
  shared `State` field is closed separately by a generation guard (see "Cross-flow state
  coordination").
- Inside `AsyncAsymmetricLock`, the bookkeeping counters (`locksHeld`, `upgradedLocksHeld`,
  `lockScope`) are guarded by an internal `lock (Lock)` monitor. Every read goes through the
  `LocksHeld`/`UpgradedLocksHeld` properties (which lock) and every mutation runs inside that
  same monitor.
- Work hops threads across `await` points, but `await` and the monitor's `Monitor.Enter/Exit`
  both insert full memory fences, so visibility within a serialized evaluation is covered.

The one deliberate exception is a **lock-free fast path** in `Get()` (in `MemoizR`,
`ConcurrentMap`, `ConcurrentMapReduce`):

```csharp
if (State == CacheState.CacheClean && Context.ReactionScope.CurrentReaction == null)
    return Value;            // returns without taking the ContextLock
```

This path is an optimization: a clean memo with no active reaction is returned without paying
for the lock. It reads `State`, `CurrentReaction`, and `Value` with **no lock held**, while
other threads may be writing `State`/`Value` under the `ContextLock`. Every field read on this
path must therefore be safe to read concurrently with a writer — covered by `volatile` for the
reference-sized fields (rule 2) and by an immutable box for the generic `Value` (rule 4).

A review found the `volatile` keyword was applied inconsistently:

- `volatile` on fields that are only ever touched under a lock (`locksHeld`,
  `upgradedLocksHeld`, `CurrentGets`, `CurrentGetsIndex`) — redundant; the lock already
  fences. The `volatile`+`Interlocked` combination on the counters is only warning-free
  because the compiler exempts `Interlocked` from CS0420.
- **No** `volatile` on `State`, even though it is read on the lock-free fast path right next to
  the `volatile CurrentReaction` — the field that most needed it didn't have it.

## Decision

We adopt the following rules for `volatile` in this codebase:

1. **A field accessed only under a lock does not get `volatile`.** The monitor (`lock (Lock)`)
   and the `ContextLock` already provide the necessary fences and atomicity. Adding `volatile`
   is redundant and misleads readers into thinking the field is accessed lock-free.
   - Applies to: `locksHeld`, `upgradedLocksHeld`. ("Only under a lock" means one *consistent*
     lock for every access — see the `CurrentGets` correction under rule 2.)

2. **A field whose accesses no single monitor covers is `volatile`** (or accessed via
   `Volatile.Read`/`Volatile.Write`) so the read has acquire visibility and the write has
   release visibility. They are kept consistent: if one field on a fast-path expression is
   volatile, all of them are.
   - Applies to: `ReactionScope.CurrentReaction` and `State` (in `MemoizR`, `ConcurrentMap`,
     `ConcurrentMapReduce`), both read on the lock-free fast path. `State` is backed by the
     shared `SignalHandlR.stateCell` (`CacheStateCell`) — a `volatile` field for the lock-free
     read, plus a `gate` monitor that serializes its transitions (the generation guard, see
     "Cross-flow state coordination") — behind an unchanged property so call sites are
     untouched. `ReactionBase`'s `State` goes through the same cell; its `isPaused` is a plain
     `volatile bool` because it is written by `Pause`/`Resume` from arbitrary threads and read in
     `Update` with no lock.
   - Also applies to: `ReactionScope.CurrentGets` / `CurrentGetsIndex`. These were briefly
     demoted to rule 1 on the claim that the `ContextLock` serializes them, but that claim is
     false for `StructuredReduceJob`: its parallel children do **not** `ForceNewScope` (only
     `StructuredResultsJob`'s do), so they share their parent flow's scope, and the per-flow
     ContextLock grants all of them *recursively and concurrently*. Writes then go through
     `CheckDependenciesTheSame` under `Context.Lock` while the job's accumulator reads under the
     job's own `Lock` — two different monitors, no happens-before edge. `volatile` supplies the
     missing release/acquire pairing; writes stay whole-array swaps (rule 3).

3. **`volatile` is never used as a substitute for atomicity.** Compound read-modify-write
   operations (e.g. `CurrentGets = [.. CurrentGets, x]`, incrementing `CurrentGetsIndex`,
   counter changes) are made atomic by the surrounding lock, not by `volatile`. `volatile` on
   an array reference publishes only the reference, not the elements — acceptable here only
   because arrays are swapped wholesale (immutable pattern).

4. **The cached `Value` is published through an immutable box behind a volatile reference.**
   `MemoHandlR<T>.Value` is generic, so it can be neither marked `volatile` nor read with
   `Volatile.Read` (the generic overload is class-constrained), and a large struct `T` could
   tear under a concurrent write. So `Value` is backed by a `private volatile ValueBox valueBox`
   where `ValueBox` is an immutable single-`readonly`-field holder. A write swaps in a
   fully-constructed box (`valueBox = new ValueBox(value)` — an atomic reference store with
   release semantics); a read takes the reference once and returns its `readonly` field. The
   `Value` property keeps all call sites unchanged.
   - **No tearing:** readers only ever observe a fully-constructed, immutable box; the value
     inside is never mutated in place.
   - **Consistency with `State`:** every `Update` writes `Value` (publishes the box) *before*
     setting `State = CacheClean` (a volatile release), and the fast path reads `State` (a
     volatile acquire) *before* `Value`. By release/acquire ordering, a reader that observes
     `CacheClean` is guaranteed to see the box of that-or-a-newer clean generation — never an
     older or partially-written one. The fast-path read is thus a **linearizable snapshot**.

5. **Locks are the primary synchronization mechanism; `volatile` only supports the deliberate
   lock-free fast path.** New shared mutable state should be protected by the appropriate lock.
   Reach for `volatile` only when you are intentionally reading a field without a lock.

## Cross-flow state coordination (the generation guard)

Because the `ContextLock` is per-flow (above), a memo node's shared `State` is **not** protected
by a single lock: a `Set` invalidating the node (`Stale` → `CacheDirty`) runs on the writer's
flow, while a `Get` recomputing it ends by writing `CacheClean` on a reader's flow. With nothing
serializing them, the recompute could commit `CacheClean` *over* a `Dirty` that arrived while it
was evaluating — leaving the memo cached-stale until the next write (for a reaction: a missed
trigger). This is a genuine lost-update, distinct from the visibility concerns above; it was
reproduced deterministically (`Memo_StaleDuringRecompute_IsNotClobbered`) and on the ARM CI runner.

`State` is therefore held in a `CacheStateCell` that guards its transitions with a monotonic
**generation**:

- The current state is exposed through a plain `volatile` read, so the lock-free `Get` fast path
  stays lock-free. Only the writers take the cell's gate.
- An **invalidation** (`Stale`) escalates the state and **always bumps the generation — even when
  the state was already at least that dirty**. The bump must be unconditional: a node sitting in
  `CacheCheck` whose suppressed `Stale` left no trace would commit `Clean` over a pending dirty
  parent, and because the cascade also stops at already-dirty nodes, nothing would ever re-dirty
  it (reproduced deterministically in `Memo_SuppressedStaleDuringParentCheck_IsNotClobbered`).
  When the state did not escalate, propagation to observers is still skipped — they were notified
  when the node first reached that state — which is safe only because of the re-notify rule below.
- An **evaluation** snapshots the generation before it commits: `UpdateIfNecessary` snapshots it up
  front via `Generation` (covering the parent-check phase), and `Update` re-snapshots via
  `BeginEvaluation` (which also marks `Evaluating`) so a `Stale` during the recompute itself is
  caught. It only commits `CacheClean` if the snapshotted generation is unchanged
  (`TryCommitClean`). If a `Stale` bumped it meanwhile, the commit is dropped and the node stays
  dirty for the next `Get` / the debounced reaction update to recompute.
- A **refused commit re-notifies the observers**
  (`SignalHandlR.CommitCleanOrRenotifyAsync`): when the final commit of an evaluation loses to a
  concurrent invalidation, an observer may have committed `Clean` against this node's
  pre-invalidation value inside the same window (its own cascade notification can have been
  suppressed at an already-dirty ancestor). Re-propagating `Stale(CacheCheck)` to observers
  closes that descendant-level window; for a reaction observer it also re-schedules the
  debounced update. This only runs on actual commit failures, so it terminates and adds no work
  to the uncontended path.
- The **diamond down-link** (a parent marking an observer dirty after it recomputed, via the
  `IMemoizR.State` setter → `InvalidateFromParent`) escalates the state **without** bumping the
  generation. When it fires during the observer's own same-flow evaluation — the observer is
  reading that very parent — it must be *absorbed*, not treated as a concurrent invalidation,
  otherwise the observer would needlessly recompute again (this is what distinguishes the benign
  diamond propagation from a real cross-flow `Set`).

This is node-local and changes no locking, so it cannot deadlock the structured-concurrency
fork/join. The cell and the commit/notify protocol live once on `SignalHandlR` and are used by
`MemoizR`, `ConcurrentMap`, `ConcurrentMapReduce`, and `ReactionBase`. `ConcurrentRace` is
exempt: it recomputes on every `Get`, so a clobbered `Clean` never yields a stale read.

Reactions add one more layer: their updates (the debounced update, `Resume()`, and
`IMemoizR.UpdateIfNecessary`) are also serialized per node by the inherited `mutex`, because two
debounced updates inherit *different* flows' ContextLocks and the generation guard protects only
the `State` commit, not the ordering of `Execute`'s side effects — without the mutex, a stale
in-flight `Execute` could apply its effects after a newer update finished and committed `Clean`.

## Consequences

Positive:

- The keyword now signals intent: a `volatile` field is one that is read without a lock.
- Removes a false sense of safety (the counters looked "extra safe" but were just redundant).
- Closes the `State` visibility gap on the fast path and makes fast-path expressions internally
  consistent.
- The lock-free `Get()` fast path is now **fully correct**, not "eventually consistent": the
  `Value` read cannot tear (immutable box) and cannot be inconsistent with the observed `State`
  (release/acquire ordering, rule 4). It returns a value that was committed at the linearization
  point of the `State` read.

Clarifications / non-issues:

- The fast path is still a point-in-time read: a writer may dirty the memo immediately *after*
  the read. This is **not** a staleness bug — in the absence of a happens-before edge between
  that write and the read, the read simply linearizes *before* the write, which is correct
  concurrent behavior. A caller that requires its own prior `Set` to be observed already has
  program-order/`await`/lock ordering to that `Set`, which makes the dirty `State` visible and
  routes the read onto the locked path.
- Memory-visibility correctness **cannot be fully proven by tests** under the .NET relaxed
  memory model. The concurrency tests (`AsyncAsymmetricLockTests` counter/invariant tests, the
  fast-path stress test `Memo_ConcurrentFastPathReads_StayConsistentAndConverge`, and the
  Coyote systematic test) guard the *logic*; the justification for each decision is the static
  release/acquire argument above plus whether the field is, or is not, accessed under a lock.

## Alternatives considered

- **Always take the `ContextLock` in `Get()` (drop the fast path).** Simplest and fully
  correct, but removes the memoization performance win for the common clean-read case. Rejected
  in favour of keeping the fast path, now that the box + `volatile` make it correct without a
  lock.
- **Make every shared field `volatile`.** Rejected: it gives a false sense of safety, does not
  make compound operations atomic, cannot be applied to the generic `Value` (hence the box), and
  obscures which fields are genuinely read lock-free.
- **Leave `Value` as a plain field and accept stale/torn reads.** This was the previous
  position. Rejected: a large struct `T` can tear, and "eventually consistent" is a weaker
  guarantee than the rest of the graph provides. The immutable box closes the gap at the cost of
  one small allocation per write, with no allocation on the read path. Note the cost lands on
  `Signal.Set` too — the write hot path — because `Signal<T>` inherits `MemoHandlR<T>`; this is
  an **accepted trade-off** (one Gen0 box per `Set`, against `Set`'s existing lock + cascade
  costs) rather than an oversight. If profiling ever shows it matters, the box can be restricted
  to nodes with a lock-free Clean fast path (signals have none — `Signal.Get` reads under
  program-order/lock edges).
- **Use `Volatile.Read`/`Volatile.Write` at call sites instead of the `volatile` keyword.**
  Equivalent semantics and more explicit per access; the keyword was kept for the per-field
  fields for brevity. Note this is not an option for `Value`: the generic `Volatile.Read<T>`
  overload is constrained to reference types, which is the other reason `Value` goes through the
  box.
