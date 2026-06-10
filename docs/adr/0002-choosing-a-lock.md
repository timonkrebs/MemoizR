# ADR 0002 — Choosing a lock: `System.Threading.Lock` vs `AsyncAsymmetricLock`

- Status: Accepted
- Date: 2026-06-06
- Deciders: MemoizR maintainers

## Context

MemoizR mixes synchronous and asynchronous concurrency, so it uses more than one kind of lock and
it is easy to reach for the wrong one. This ADR records which primitive to use and why. Picking
wrong is not a style nit: holding a synchronous lock across an `await`, or using a heavyweight
async lock for a two-line field swap, leads to deadlocks, lost wake-ups, or needless overhead.

There are three primitives in play:

| Primitive | Sync/async | Hold across `await`? | Reentrant? | Modes | Cost |
|---|---|---|---|---|---|
| `System.Threading.Lock` (`lock (x) { … }`) | **synchronous** monitor | **No** (and the compiler forbids `await` inside `lock`) | Yes (same thread) | mutual exclusion | cheap |
| `Nito.AsyncEx.AsyncLock` (the per-node `mutex`) | **async** | **Yes** | No | mutual exclusion | medium |
| `AsyncAsymmetricLock` (the `ContextLock`) | **async** | **Yes** | Yes, per async flow (scope) | upgradeable + exclusive | heaviest |

Two facts drive everything below:

1. **A `lock`/monitor already provides memory visibility, not just mutual exclusion.** Its
   acquire/release fences publish one thread's writes to the next thread that takes the lock, so
   state guarded *only* by a consistent lock needs no `volatile` (see ADR 0001). "Different
   threads touch it" is exactly what locks are for.
2. **`System.Threading.Lock` is thread-affine**: it must be released on the thread that acquired
   it. An `await` can resume on a different thread, so a synchronous lock can never span one. The
   `lock` statement enforces this at compile time. Work that must stay mutually excluded *across*
   an `await` therefore needs an async lock.

## Decision

Choose by answering, in order:

### 1. Does the critical section contain an `await`?

- **No** → use **`System.Threading.Lock`**. This is the default for guarding in-memory state with
  short, synchronous critical sections. It is the cheapest, it gives full memory fences, and it
  is correct across threads.
- **Yes** → you need an async lock; continue to step 2.

### 2. (Async) Do you need reader/writer asymmetry and recursion within the same async flow?

- **No — plain mutual exclusion is enough** → use **`Nito.AsyncEx.AsyncLock`** (the `mutex`
  pattern). One holder at a time, may be held across awaits. Use it to serialize a single
  operation that spans awaits but does not need shared/exclusive modes.
- **Yes — you are serializing the reactive graph** → use **`AsyncAsymmetricLock`** (the
  `ContextLock`). It distinguishes *upgradeable* acquisitions (reads / recompute, taken with
  `UpgradeableLockAsync`) from *exclusive* ones (writes, taken with `ExclusiveLockAsync`), and it
  is **reentrant within one async flow** (tracked by an `AsyncLocal` scope), so nested
  `Get`/`UpdateIfNecessary` on the same flow do not self-deadlock. See the class docstrings for
  the exact mode-compatibility matrix.

### Rules of thumb

- **Default to `System.Threading.Lock`.** Reach for an async lock only when the critical section
  genuinely must stay held across an `await`.
- **Never block on an async lock** (`.LockAsync().GetAwaiter().GetResult()`, `.Result`, `.Wait()`)
  — that reintroduces the thread-pool starvation the async lock exists to avoid.
- **Never try to hold a `System.Threading.Lock` across an `await`.** If you find yourself wanting
  to, the state either needs an async lock, or it should be split so the awaited work happens
  outside the lock (this is how `AsyncAsymmetricLock` itself works: the synchronous bookkeeping is
  under `lock (Lock)`, and the actual waiting happens on an awaited `TaskCompletionSource` *after*
  the monitor is released).
- **Publish shared state by exactly one discipline, consistently.** A field that is read while
  another thread/flow may be writing it must be made safe by one of three mechanisms (see
  "Publishing shared state" below). The bug to avoid is a field that is written under a lock but
  *also* read (or written) on a path that no barrier covers.
- **One `AsyncAsymmetricLock` per `Context`-flow, not per field.** It is the graph-serialization
  lock; do not use it as a general-purpose mutex.

## How MemoizR applies this

**`System.Threading.Lock`** — short synchronous critical sections guarding in-memory state, no
`await` inside:

- `AsyncAsymmetricLock.Lock` — the async lock's own counters (`locksHeld`, `upgradedLocksHeld`,
  `lockScope`).
- `Context.Lock` — the `AsyncReactionScopes` dictionary, scope creation, and
  `CheckDependenciesTheSame`.
- `SignalHandlR.Lock` — the `IMemoHandlR.Observers`/`Sources` *interface* setters, which exist to
  guard array swaps made by parallel structured-concurrency children. (Note this lock is not the
  sole guard of those fields — see "Publishing shared state".) `Signal.Lock` /
  `EagerRelativeSignal.Lock` — the cached `Value` write.
- `CacheStateCell` (the `gate`) — a memo's `State`/generation transitions (the lost-update guard,
  ADR 0001/follow-up).
- `MemoFactory.Lock`; `StructuredResultsJob`/`StructuredReduceJob.Lock` (accumulating sources
  across concurrent child tasks); `StructuredResourceGroup`'s `mutex`.
- `ReactionBase` uses `lock (this)` to make the `Stale` state change and debounce-token swap
  atomic.

**`Nito.AsyncEx.AsyncLock` (`mutex`)** — serialize a single node's / job's work that spans awaits,
plain mutual exclusion:

- `SignalHandlR.mutex` (inherited by every node — the memos, `ConcurrentMap`,
  `ConcurrentMapReduce`, `ConcurrentRace`, and `ReactionBase`) — ensures only one evaluation of a
  given node runs at a time, held across the awaited recompute. For reactions this is what orders
  `Resume()` against concurrently scheduled debounced updates: those inherit *different* flows'
  ContextLocks, and the generation guard protects only the `State` commit, not the ordering of
  `Execute`'s side effects.
- `StructuredJobBase.mutex`.

**`AsyncAsymmetricLock` (`ContextLock`)** — serialize evaluation of the reactive graph, held across
the whole awaited evaluation:

- Taken **exclusive** by writes: `Signal.Set`, `EagerRelativeSignal.Set`.
- Taken **upgradeable** by reads/recompute: `MemoizR.Get`/`UpdateIfNecessary`,
  `ConcurrentMap`/`ConcurrentMapReduce`/`ConcurrentRace`, and `ReactionBase`
  (`Resume`/`UpdateIfNecessary`/the debounced update).

Note the `ContextLock` lives on the per-flow `ReactionScope` (intentionally — a single shared lock
would deadlock structured-concurrency children against the parent awaiting them). The cross-flow
state coordination this leaves open is handled by the `CacheStateCell` generation guard, not by
widening the lock (see ADR 0001, "Cross-flow state coordination").

## Publishing shared state

Picking the right *lock* is only half the story; a field read concurrently with a write also needs
its writes *published* to the reader. MemoizR uses three disciplines — a field should rely on
exactly one, applied to every access:

1. **One consistent lock.** Every read and write takes the same monitor. The release/acquire
   fences publish the writes; no `volatile` is needed (ADR 0001). This is the common case
   (`locksHeld`, `Context`'s dictionary, `CacheStateCell`'s state under its `gate`).

2. **`volatile` / `Volatile.Read` for an intentional lock-free read.** Used only on the deliberate
   `Get()` fast path: `State`, `CurrentReaction`, the `ValueBox` reference (ADR 0001).

3. **A happens-before barrier — a fork/join or a higher-level lock that already serializes all
   access.** No per-field lock is needed because something coarser orders the accesses:
   - `StructuredJobBase.result` and the jobs' source accumulation are written by child tasks
     during the fork, then read **without** a lock after `await Task.WhenAll(...)` — the join is
     the barrier. The concurrent *writes* during the fork are still made safe per job, not left
     unsynchronized: `StructuredReduceJob` folds into its `result` under the job's `lock (Lock)`,
     while `StructuredResultsJob`'s `result` is a `ConcurrentDictionary` written with `TryAdd`
     (the collection's own internal synchronization — discipline (1) applied by the collection).
     Both jobs accumulate `allSources` and rewire observer links under the shared
     `StructuredJobBase.Lock` (`AccumulateSourcesAndObservers`). Note the *inputs* to that
     accumulation, `ReactionScope.CurrentGets`/`CurrentGetsIndex`, are written under
     `Context.Lock` — a different monitor — which is why those two fields are `volatile`
     (discipline (2); see ADR 0001 rule 2).
   - `SignalHandlR.Sources` / `Observers` are mutated and read **lock-free during normal graph
     evaluation**, which is serialized per flow by the `ContextLock`; writes are also whole-array
     swaps (an atomic reference publish). The `SignalHandlR.Lock` on the `IMemoHandlR` interface
     setters covers only the extra case of *parallel* structured-concurrency children appending to
     the same observer array. So these fields are guarded by (3) + atomic swaps, **not** by
     `SignalHandlR.Lock` alone.

The rule of thumb is the test: if you cannot name which of these three covers a field, it is a
bug. What is *not* allowed is a field written under a lock and also read on a path that none of the
three covers.

## Consequences

- A reader can decide in one question ("is there an `await` in the critical section?") which family
  to use, and a second to pick the async variant.
- The synchronous `System.Threading.Lock` stays the common case, keeping most critical sections
  cheap and easy to reason about.
- The two async locks are reserved for the two things that genuinely need to stay locked across
  awaits: serializing one node's evaluation (`mutex`) and serializing the graph (`ContextLock`).
- The guidance is only safe if each piece of state is published by exactly one of the three
  disciplines above. The two that are not "one consistent lock" — the `volatile` fast-path reads
  (ADR 0001) and the join/`ContextLock`-serialized fields (`result`, `Sources`/`Observers`) — are
  called out explicitly so they are not mistaken for unguarded access.
