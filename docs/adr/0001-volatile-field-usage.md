# ADR 0001 — Use of `volatile` fields in MemoizR

- Status: Accepted
- Date: 2026-06-06
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
other threads may be writing `State`/`Value` under the `ContextLock`.

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

4. **The cached `Value` is a known, accepted limitation.** `MemoHandlR<T>.Value` is generic, so
   `volatile` is not even legal on it, and a large value-type `T` could tear if read
   concurrently. Its lock-free fast-path read is accepted as *eventually consistent*: it may
   return a recent-but-stale value. Correctness of recomputation is guaranteed on the slow
   (locked) path. If strict visibility is ever required, use `Volatile.Read`/a lock at the read
   site rather than the keyword.

5. **Locks are the primary synchronization mechanism; `volatile` only supports the deliberate
   lock-free fast path.** New shared mutable state should be protected by the appropriate lock.
   Reach for `volatile` only when you are intentionally reading a field without a lock.

## Consequences

Positive:

- The keyword now signals intent: a `volatile` field is one that is read without a lock.
- Removes a false sense of safety (the counters looked "extra safe" but were just redundant).
- Closes the `State` visibility gap on the fast path and makes fast-path expressions internally
  consistent.

Negative / accepted trade-offs:

- The `Get()` fast path is inherently **time-of-check/time-of-use**: even with `volatile State`,
  a writer can dirty the memo immediately after the check, so a stale-but-valid `Value` may be
  returned. This is an accepted property of the optimization, not a bug. Full linearizability
  would require always taking the `ContextLock`.
- `Value` remains outside the memory-model guarantees that `volatile` could give (see rule 4).
- Memory-visibility correctness **cannot be proven by tests** under the .NET relaxed memory
  model. The concurrency tests (`AsyncAsymmetricLockTests` counter/invariant tests, the
  fast-path stress test `Memo_ConcurrentFastPathReads_StayConsistentAndConverge`, and the
  Coyote systematic test) guard the *logic*; the justification for each `volatile` decision is
  the static argument that the field is, or is not, accessed under a lock.

## Alternatives considered

- **Always take the `ContextLock` in `Get()` (drop the fast path).** Simplest and fully
  correct, but removes the memoization performance win for the common clean-read case. Rejected
  for now in favour of keeping the fast path with explicit `volatile`.
- **Make every shared field `volatile`.** Rejected: it gives a false sense of safety, does not
  make compound operations atomic, cannot be applied to the generic `Value`, and obscures which
  fields are genuinely read lock-free.
- **Use `Volatile.Read`/`Volatile.Write` at call sites instead of the `volatile` keyword.**
  Equivalent semantics and more explicit per access; the keyword was kept for the per-field
  fields for brevity, and `Volatile.Read` remains the recommended escape hatch for `Value`.
