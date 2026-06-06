# ADR 0001 — Use of `volatile` fields in MemoizR

- Status: Accepted
- Date: 2026-06-06
- Updated: 2026-06-06 — the lock-free `Value` read is now made tear-free and consistent
  (rule 4); it is no longer an accepted "eventually consistent" limitation.
- Deciders: MemoizR maintainers

## Context

MemoizR is a concurrent reactive graph. Its thread-safety rests almost entirely on **locks**,
not on lock-free field access:

- Per `Context`, all graph evaluation (reading/recomputing memos, rewiring sources/observers)
  is serialized by the `ContextLock`, an `AsyncAsymmetricLock` taken as upgradeable for reads
  and exclusive for writes. Only one flow evaluates a given context's graph at a time.
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
   - Applies to: `locksHeld`, `upgradedLocksHeld`, `ReactionScope.CurrentGets`,
     `ReactionScope.CurrentGetsIndex`.

2. **A field that is read on the lock-free fast path is `volatile`** (or accessed via
   `Volatile.Read`/`Volatile.Write`) so the read has acquire visibility and the write has
   release visibility. They are kept consistent: if one field on a fast-path expression is
   volatile, all of them are.
   - Applies to: `ReactionScope.CurrentReaction` and `State` (in `MemoizR`, `ConcurrentMap`,
     `ConcurrentMapReduce`). `State` is backed by a `volatile` field behind an unchanged
     property so call sites are untouched. `ReactionBase.State`/`isPaused` are also volatile
     because they are touched under *two different* locks / from arbitrary threads.

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
  one small allocation per write (writes are far rarer than reads on a memo, and `Update`
  already allocates), with no allocation on the read path.
- **Use `Volatile.Read`/`Volatile.Write` at call sites instead of the `volatile` keyword.**
  Equivalent semantics and more explicit per access; the keyword was kept for the per-field
  fields for brevity. Note this is not an option for `Value`: the generic `Volatile.Read<T>`
  overload is constrained to reference types, which is the other reason `Value` goes through the
  box.
