# MemoizR Performance Notes

This document records the measurement-driven performance round on the hot paths: what the
baseline revealed, the root causes, each fix and its safety argument, the resulting numbers,
and what was deliberately left on the table. It complements the
[Concurrency Architecture](concurrency.md) — every optimization here preserves the protocol
documented there, and the safety arguments below lean on its sections.

## Results

Same workload before and after, on a shared dev container (numbers are indicative, not lab-grade;
multiple runs shown as ranges where variance was significant):

| Benchmark | Before | After | Factor |
|---|---:|---:|---:|
| clean `Get`, single thread | 113,800 ns/op | 60–107 ns/op | ~1,400× |
| clean `Get`, 4 parallel flows | 128,260 ns/op | 32–60 ns/op | ~3,000× — now scales with cores |
| clean `Get` allocations | 706 B/op | **0 B/op** | allocation-free |
| `Set` + dirty `Get` recompute cycle | 65,558 ns/op | ~26,000 ns/op | ~2.5× |
| `Set` (no observers) | 516 ns/op | 592–2,143 ns/op | unchanged (run variance; code path untouched) |

The headline: the clean read — *the* hot path of a memoization library — went from a hundred
microseconds with allocations to double-digit nanoseconds with none, and stopped serializing
across threads.

## What the baseline revealed

The "lock-free fast path" was a fiction in practice. The fast path read two volatile fields, but
*reaching* it cost a fortune, for a subtle `AsyncLocal` reason:

1. **The flow pin never survives a top-level `Get`.** `Get` pins a scope key onto the flow via
   an `AsyncLocal` — but the write happens *inside the async `Get` frame*, and `AsyncLocal`
   mutations made inside an async callee do not propagate to the caller. So for every top-level
   caller the flow is unpinned again the moment `Get` returns. (The pin works as designed for
   everything *inside* one logical operation — nested reads, structured children — which is its
   actual job.)
2. **Every top-level `Get` therefore minted a scope**: a `ReactionScope` + its
   `AsyncAsymmetricLock`, registered in the scope dictionary under a key that could never be
   resolved again — pure garbage, ~700 B per read.
3. **The registry swept itself on every mint** — an O(table) walk of all entries to remove dead
   ones, under the context-wide lock. Sustained reads grew the table faster than entries died,
   so each read scanned an ever-larger table: **quadratic**, measured at 113 µs/op average over
   2M reads.
4. Every scope resolution (and the dependency-capture bookkeeping) took the single context-wide
   monitor, so parallel readers convoyed on one lock.

## The fixes

### 1. Scope-free fast path (the big one)

An **unpinned flow can have no capturing reaction**: its scope would be freshly minted, and a
fresh scope has `CurrentReaction == null` by construction. So a clean read from an unpinned flow
needs *no scope at all*:

```csharp
if (State == CacheState.CacheClean && !Context.HasFlowScope)
{
    return Value;   // two volatile reads; no scope, no lock, no allocation
}
```

`HasFlowScope` is a plain `AsyncLocal` read. The memory-model argument is unchanged from the
[concurrency doc §7](concurrency.md#7-the-read-path-lock-free-fast-path-and-its-memory-model):
volatile `State` (acquire) before the volatile `ValueBox` reference — the flow check plays no
ordering role. Applied to `MemoBase<T>.Get`, `Signal.Get`, and `EagerRelativeSignal.Get`.

### 2. Lock-free scope resolution

The scope registry became a `ConcurrentDictionary`, taking scope resolution off the context-wide
monitor entirely — this is what made parallel reads scale instead of convoying. Resurrecting a
collected scope under a still-pinned key is atomic (a `GetOrAdd`/`TryUpdate` loop on the exact
dead entry), because several tasks can share one flow key concurrently (debounced reaction
updates inherit the triggering `Set`'s flow) and two racing a dead entry must agree on ONE fresh
scope — a last-write-wins overwrite would hand them different `ContextLock`s.

### 3. Amortized registry sweeping

Scopes are held weakly, but the dictionary *entries* must still be removed. Instead of sweeping
on every registration, the sweep now fires only when the registrations since the last sweep
rival the table size — O(1) amortized per mint, table bounded by ~2× the live-flow count.

Note the sweep's contract (this trips people up in tests): `PruneDeadScopes` removes entries
whose scope the GC **has actually collected** — it is a dead-entry sweep, not an eviction
policy. If the scopes are still reachable (e.g. under an attached debugger or debug-build
codegen, which keep stack slots — including intermediate `Task<ReactionScope>` results that
strongly reference the scopes — alive to end of method), the count will not shrink, by design.
The boundedness test runs the sweep explicitly after a real collection for determinism.

### 4. Lock-free generation snapshots

`CacheStateCell.Generation` was a monitor acquisition just to read an `int`, paid on every
`UpdateIfNecessary`. It is now a volatile read. Safety argument: a volatile snapshot can only be
**stale-low** (it can never observe an unwritten bump), and a stale-low token makes the eventual
`TryCommitClean` — which compares under the gate — *refuse* the commit. That is exactly the
conservative outcome of a racing invalidation: a spurious extra recompute, never a wrongful
commit. The lost-update guard's strength is untouched.

### 5. De-asynced protocol helpers

`CommitCleanOrRenotifyAsync` and `InvalidateAndPropagateAsync` are now plain `Task`-returning
methods with synchronous fast-outs (`Task.CompletedTask`), so the common cases — suppressed
invalidation, already-Clean commit — pay no async state machine and, for the commit, skip the
cell gate entirely: if the state is already `CacheClean`, either this token's early commit
succeeded (every invalidation escalates the state away from Clean, so Clean implies an unchanged
generation) or a newer evaluation committed; either way there is nothing to do.

### 6. Structured job completion built once

`StructuredJobBase.Run` builds its `Task.WhenAll` completion exactly once and reuses it on the
fault path; previously the `catch` re-ran the `Select`, allocating a second set of unwrap
wrappers — and could even await never-started cold tasks if `AddConcurrentWork` itself had
thrown.

## Two bugs the speedup exposed

Making the scope machinery fast shifted thread timings enough to flush out two **pre-existing
races**. The general lesson stands: **performance changes are scheduling changes** — rerun the
concurrency suite hard after them.

1. **Cold-task start vs. instant failure** (`StructuredJobBase.Run`): a child task that starts
   and fails *instantly* cancels the group token while the start loop is still running, which
   transitions the not-yet-started cold siblings to `Canceled` — and `Task.Start()` on a
   completed task throws `InvalidOperationException`.
   `FailingJobThrowsAggregateContainingFault` flaked exactly there. The fix skips non-`Created`
   tasks and guards the unavoidable check-to-`Start` window; a pre-start-canceled child now
   behaves identically to one canceled mid-run.

2. **The first-evaluation subscription window.** A node used to wire itself into its sources'
   observer lists only *after* its evaluation completed — so a `Set` landing between the read
   and the wiring saw **no observer and notified nobody**. The node then committed a value
   computed from the pre-`Set` read, with no `Stale` ever bumping its generation (it wasn't
   subscribed, so the entire [generation guard](concurrency.md#6-the-heart-cross-flow-correctness-of-state)
   was bypassed), and cached stale until some *later* unrelated write happened to land. The
   sync-context reaction test flaked on exactly this (~1-in-6 once timings shifted); a repro
   loop plus generation-arithmetic forensics (`Generation == 2` where a delivered `Stale` would
   have made it 3) pinned it. **Fix: subscribe at capture time** — the observer link is added
   the moment a new source is first read (`CheckDependenciesTheSame`), so a mid-evaluation `Set`
   reaches the half-built node, bumps its generation, the commit is refused, and the normal
   machinery re-runs. The deferred rewiring keeps its merge/removal job and became
   add-if-absent. Deterministic regression:
   `Memo_FirstEvaluation_SetDuringSubscriptionWindow_IsNotLost` (verified to fail with the eager
   subscription disabled).

## Deliberately left on the table

Weighed and skipped, because the risk outweighs µs-scale gains off the hot path:

- **Replacing the per-node Nito `AsyncLock`** with a slimmer custom mutex (allocation per
  contended acquisition). The lock is correct and battle-tested; the recompute path it sits on
  is dominated by user computation anyway.
- **Eliding the throwaway-scope lock acquisition in unpinned `Set`.** Locking a lock nobody else
  can reference synchronizes nothing, so it could be skipped — but the lock acquisition also
  mints the flow's lock-scope identity that the `Stale` cascade's debounce tasks inherit, and
  the interaction is subtle enough that the ~1 µs saving doesn't justify it without its own
  verification round.
- **CAS-packing `CacheStateCell`** (state + generation in one `long`). The gate is uncontended
  in practice and never held across `await`; the monitor costs ~20 ns.
- **`Task.Unwrap()` in the jobs** instead of the `async t => await await t` wrappers: the
  explicit shape exists because the Coyote rewriter does not honour the alternatives faithfully.

## Reproducing the measurements

There is deliberately no benchmark project in the solution (it would drag BenchmarkDotNet into
CI); the harness is five minutes to recreate. Console app referencing the `MemoizR*` projects,
`Release`, sections warmed once and measured once:

```csharp
var f = new MemoFactory();
var v = f.CreateSignal(1);
var m = f.CreateMemoizR(async () => await v.Get() * 2);
await m.Get(); // prime

// clean Get, single thread:          loop `await m.Get()` 2M times
// clean Get, parallel:               4x Task.Run, 500k each
// Set (no observers):                fresh signal, 100k awaited Sets
// recompute cycle:                   fresh signal+memo, 50k x (Set; Get)
// allocations:                       GC.GetTotalAllocatedBytes(true) around 100k clean Gets
```

Treat absolute numbers as environment-relative; what must hold are the *shapes*: clean reads
allocation-free and flat under thread count, and no superlinear growth of read cost over time
(the quadratic-prune signature this round eliminated).
