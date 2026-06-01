# MemoizR — Relentless Code Review

**Date:** 2026-06-01
**Scope:** all 28 source files across the 4 libraries (`MemoizR`, `MemoizR.Reactive`, `MemoizR.StructuredConcurrency`, `MemoizR.StructuredAsyncLock`).
**Method:** 8 failure-mode lenses → 57 raw findings → adversarial verification (default-refute) → 37 survived → dedup/synthesis, plus independent verification of the linchpin claims against the source.

**Baseline:** `dotnet build` succeeds (0 errors, 2 cosmetic `CS1998` warnings in `MemoizR.Tests/ResourceManagementTests.cs`). The bugs below are **not** caught by the compiler and largely not by the existing tests (which mostly run single-threaded or tolerate the defects by coincidence).

The headline: **the central "automatic synchronization" guarantee is structurally broken**, and several reactive nodes silently serve stale values. Severity reflects a calibrated assessment of real-world blast radius.

---

## Summary table

| # | Severity | Title | File |
|---|----------|-------|------|
| C1 | 🔴 Critical | `ReactionScope` getter never persists its scope key → global lock excludes nothing + unbounded leak | `MemoizR/Context.cs:32` |
| C2 | 🔴 Critical | `SuppressThrowing` swallows upstream faults → node `CacheClean` with stale value | `MemoizR/MemoizR.cs:75` |
| H1 | 🟠 High | `StructuredReduceJob` shares one `ReactionScope` across parallel tasks (no `ForceNewScope`) | `MemoizR.StructuredConcurrency/StructuredReduceJob.cs:23-70` |
| H2 | 🟠 High | `Observers`/`Sources` read & reassigned outside the per-instance `Lock` | `MemoizR/MemoizR.cs:164-214` |
| H3 | 🟠 High | `Get()` fast-path reads non-volatile `State` | `MemoizR/MemoizR.cs:19` |
| H4 | 🟠 High | Race-job `finished` flag is non-`volatile`; a loser cancels the whole group | `MemoizR.StructuredConcurrency/StructuredRaceJob.cs:9` |
| H5 | 🟠 High | `result` written without synchronization in the race job | `MemoizR.StructuredConcurrency/StructuredRaceJob.cs:44` |
| H6 | 🟠 High | `ConcurrentRace` never collects sources → never invalidated (truly detached) | `MemoizR.StructuredConcurrency/ConcurrentRace.cs:50-110` |
| H7 | 🟠 High | `StructuredJobBase.Run`: unstarted-task hang + exception mangling + sync-lock-across-await | `MemoizR.StructuredConcurrency/StructuredJobBase.cs:20-41` |
| H8 | 🟠 High | Job finalizers cancel the **shared** Context CTS from the GC thread | `ConcurrentRace.cs:144`, `ConcurrentMap.cs:187`, `ConcurrentMapReduce.cs:225` |
| H9 | 🟠 High | `ReactiveMemoFactory.AddSynchronizationContext`: `.Add` throws on re-call + static-dict leak | `MemoizR.Reactive/ReactiveMemoFactory.cs:7,13` |
| M1 | 🟡 Medium | Dead invariant guard in `RequestExclusiveLockAsync`; real path throws "Should never happen!" | `MemoizR.StructuredAsyncLock/AsyncAsymmetricLock.cs:77-102` |
| M2 | 🟡 Medium | Async-lock cancellation is entirely dead-wired | `MemoizR.StructuredAsyncLock/Nito/AsyncWaitQueue.cs:63` |
| M3 | 🟡 Medium | `ReactionBase` debounce is defeated; continuation runs in a disconnected scope | `MemoizR.Reactive/ReactionBase.cs:200-229` |
| M4 | 🟡 Medium | `ConcurrentMap` never prunes observer links on re-evaluation | `MemoizR.StructuredConcurrency/ConcurrentMap.cs:106-153` |
| M5 | 🟡 Medium | `StructuredResourceGroup` swallows all disposal exceptions, even on success | `MemoizR.StructuredConcurrency/StructuredResourceGroup.cs:31-62` |
| M6 | 🟡 Medium | Reduce seed is `default(T)` with no identity contract | `MemoizR.StructuredConcurrency/StructuredReduceJob.cs:33` |
| M7 | 🟡 Medium | Reduce delegate signature mismatch (factory vs. implementation) | `StructuredConcurrencyFactory.cs:20,25` vs `ConcurrentMapReduce.cs:11` |
| M8 | 🟡 Medium | `Context.Untrack` re-reads `ReactionScope` three times | `MemoizR/Context.cs:107-133` |
| M9 | 🟡 Medium | Dead `WeakReference` entries in `Observers` never pruned in steady state | `MemoizR/MemoizR.cs:167-214` |
| M10 | 🟡 Medium | `MemoFactory.CONTEXTS` retains dead string keys | `MemoizR/MemoFactory.cs:7` |
| M11 | 🟡 Medium | `StructuredRaceJob` silently swallows losers' exceptions once `finished` | `MemoizR.StructuredConcurrency/StructuredRaceJob.cs:42-55` |
| L1 | ⚪ Low | Inverted variable name `hasCurrentGets` | `MemoizR/Context.cs:78` |

---

## 🔴 Critical

### C1 — `ReactionScope` getter never persists its scope key → the global lock excludes nothing + unbounded leak
`MemoizR/Context.cs:32` (getter 26-48), interacts with `MemoizR/Signal.cs:15`, `MemoizR/EagerRelativeSignal.cs:17`

The getter computes `var key = AsyncLocalScope.Value == 0 ? rand.NextDouble() : AsyncLocalScope.Value;` but **never assigns the new key back** to `AsyncLocalScope.Value` (contrast `CreateNewScopeIfNeeded` at `Context.cs:52`, which does). The `ContextLock` lives *inside* `ReactionScope` (`Context.cs:11`), so this one omission cascades into three failures:

- **The serialization invariant is void.** `Signal.Set` / `EagerRelativeSignal.Set` take `Context.ReactionScope.ContextLock.ExclusiveLockAsync()` **without** first calling `CreateNewScopeIfNeeded`. With `AsyncLocalScope.Value == 0`, every access mints a *fresh* `ReactionScope` with a *fresh* `ContextLock`. So a `Set` locks lock instance L1 while a concurrent `Get` (which *does* establish a scope, lock L2) holds a different lock. The comment *"Only one thread should evaluate the graph at a time"* (`MemoizR.cs:24`) is not enforced between a writer and a reader — exactly the cross-thread case the README sells as "Automatic Synchronization."
- **Memory leak.** Each transient access inserts a new random-keyed entry into `AsyncReactionScopes`. `CleanScope()` only removes `AsyncLocalScope.Value`'s entry (`Context.cs:70`) and is called **only** from the reaction debounce path (`ReactionBase.cs:224`) — never from `Get`/`Set`/jobs. The dictionary grows without bound.

**Fix:** persist the key — `var key = AsyncLocalScope.Value == 0 ? (AsyncLocalScope.Value = rand.NextDouble()) : AsyncLocalScope.Value;` — and call `CreateNewScopeIfNeeded()` in both `Set` methods before touching `ContextLock`. Strongly consider replacing the `Dictionary<double, WeakReference<ReactionScope>>` with a plain `AsyncLocal<ReactionScope>`, which eliminates the keying, the collision risk, and the leak in one move. *(Independently verified.)*

### C2 — `SuppressThrowing` swallows upstream faults, leaving a node `CacheClean` with a stale value
`MemoizR/MemoizR.cs:75` (also `ReactionBase.cs:55`, `ConcurrentMap.cs:80`, `ConcurrentMapReduce.cs:82`)

When a parent's `Update()` throws, its `catch` sets `State = CacheCheck` and rethrows (`MemoizR.cs:151-154`) **without** changing its value — so it never marks this node dirty. The child awaits the parent with `ConfigureAwaitOptions.SuppressThrowing` and never inspects the result, so the loop falls through, the `CacheDirty` branch is skipped, and `MemoizR.cs:97` unconditionally sets `State = CacheClean`. **The node now reports clean while holding a pre-error value, and stays stale until something else marks it dirty.** A memoization library silently returning wrong results is the worst failure mode it has. The correct pattern already exists in `StructuredJobBase.cs:35-41`, which *inspects* `t.Exception` after suppressing.

**Fix:** in the `UpdateIfNecessary` source loop, either let the parent exception propagate, or capture the faulted task and propagate the fault / mark this node dirty before reaching the `CacheClean` assignment. *(Independently traced.)*

---

## 🟠 High

### H1 — `StructuredReduceJob` shares one `ReactionScope` across parallel tasks (no `ForceNewScope`)
`MemoizR.StructuredConcurrency/StructuredReduceJob.cs:23-70`

All cold tasks run concurrently via `Parallel.ForEach(...Start())` (`StructuredJobBase.cs:26`). Unlike `StructuredResultsJob`, which isolates each task with `context.ForceNewScope()` (`StructuredResultsJob.cs:30`), the reduce job omits it — every task mutates the **same** `CurrentGets`/`CurrentGetsIndex` and reads `CurrentGetsIndex` as a loop bound (`StructuredReduceJob.cs:56`) against an array another task may have replaced (→ lost source tracking / `IndexOutOfRange`). The job's `lock(Lock)` does not serialize against `CheckDependenciesTheSame`, which holds *Context's* lock. The observer read-modify-write at `StructuredReduceJob.cs:60-62` is also non-atomic → lost observer links.

**Fix:** add `context.ForceNewScope()` per task lambda; merge sources in `HandleSubscriptions`; make the observer RMW atomic under the source's own per-node lock.

### H2 — `Observers`/`Sources` read & reassigned outside the per-instance `Lock`
`MemoizR/MemoizR.cs:164-173` & `208-214`, `186`; `ConcurrentMapReduce.cs:128-155`

The fields are only guarded inside the `MemoHandlR` property setter (`MemoHandlR.cs:26-36`). But `Update`/`Stale` iterate `Observers` and directly reassign `source.Observers`/`Sources` while holding only `mutex`+`ContextLock` (not the per-instance `Lock`), and `ReactionBase.Dispose` → `RemoveParentObservers` holds **no** context lock at all. A source's `Observers` can be swapped between a length-check and the `foreach` → torn read, skipped notifications (missed invalidation), or `IndexOutOfRange`.

**Fix:** snapshot under the `Lock` before iterating; route all writes through the locked setter.

### H3 — `Get()` fast-path reads non-volatile `State`
`MemoizR/MemoizR.cs:19` (field `MemoizR.cs:5`)

`State` is a plain auto-property read lock-free on the fast path, while `Stale()` writes it under `ExclusiveLock` (`MemoizR.cs:206`) and `Update` writes it under `mutex` (`MemoizR.cs:97/116/118`) — three disjoint sync domains, no `volatile`. A reader can observe a stale `CacheClean` and return outdated data (visibility is weak on ARM).

**Fix:** mark the backing field `volatile`, or read only under a common lock (depends on C1 being fixed first).

### H4 — Race-job `finished` flag is non-`volatile`; a loser cancels the whole group
`MemoizR.StructuredConcurrency/StructuredRaceJob.cs:9` (write `45`, read `50`)

The winner sets `finished = true` then cancels the inner CTS. A loser catching `OperationCanceledException` checks `if (!finished)` with no barrier, may read stale `false`, and calls `groupCancellationTokenSource.Cancel()` (`StructuredRaceJob.cs:52`) — cancelling the **outer** group (shared CTS) even though a winner succeeded.

**Fix:** `Interlocked.CompareExchange` to claim the win, or model with a `TaskCompletionSource`. *(Independently verified.)*

### H5 — `result` written without synchronization in the race job
`MemoizR.StructuredConcurrency/StructuredJobBase.cs:8` (read `31`), `StructuredRaceJob.cs:44`

Reduce uses `lock`, Results uses `ConcurrentDictionary`, but the race job writes `result = await x(...)` from multiple tasks unsynchronized. Combined with H4 (multiple tasks may believe they won), the post-join read can observe a torn/not-yet-published value for reference/large-struct `T`.

**Fix:** fold the write into the same atomic claim as `finished`.

### H6 — `ConcurrentRace` never collects sources → never invalidated (truly detached)
`MemoizR.StructuredConcurrency/ConcurrentRace.cs:50-110`

`StructuredRaceJob` has no `allSources`/`HandleSubscriptions`, and `ConcurrentRace.Update`'s observer-add loop (`ConcurrentRace.cs:72-82`) iterates `Sources`, which is **never populated**. So the race node is never registered as an observer of the memos it reads → source changes never invalidate it → permanently stale result.

**Fix:** add an `allSources` accumulator + `HandleSubscriptions` to the race job (mirror `StructuredResultsJob`), and relink as `MemoizR.Update` does.

### H7 — `StructuredJobBase.Run`: unstarted-task hang + exception mangling + sync-lock-across-await
`MemoizR.StructuredConcurrency/StructuredJobBase.cs:20-41`

Three faults in one method:
- **(a) Unstarted-task hang.** Tasks are created cold and started only at `StructuredJobBase.cs:26`; if `AddConcurrentWork` throws after queuing some, the `catch` awaits `WhenAll` over **never-started** tasks → **hangs forever**.
- **(b) Exception mangling.** The catch re-runs `WhenAll` then `throw t.Exception` (`StructuredJobBase.cs:39`) → re-wraps in `AggregateException` and loses the original stack (use bare `throw;`).
- **(c) Sync-lock-across-await.** The synchronous `mutex.Lock()` (`StructuredJobBase.cs:21`) is held across `await AddConcurrentWork` and `Parallel.ForEach` — every sibling type uses `await LockAsync()`; latent deadlock if a worker ever re-enters.

**Fix:** start tasks as added (or start-then-rethrow on partial failure); preserve the stack; use `LockAsync`.

### H8 — Job finalizers cancel the **shared** Context CTS from the GC thread
`ConcurrentRace.cs:144-147`, `ConcurrentMap.cs:187-190`, `ConcurrentMapReduce.cs:225-228`

`~Type() => Cancel() → Context.CancellationTokenSource?.Cancel()`. That CTS is shared across all nodes in the Context. If a job becomes collectible while the Context still drives live work, the finalizer **cancels unrelated in-flight computations** nondeterministically. (The `?.` does prevent the disposed-CTS crash, so this is high, not critical.)

**Fix:** remove the finalizers; make cancellation explicit, or give each job its own CTS disposed in `finally`.

### H9 — `ReactiveMemoFactory.AddSynchronizationContext`: `.Add` throws on re-call + static-dict leak
`MemoizR.Reactive/ReactiveMemoFactory.cs:7,13`

`Dictionary.Add` throws `ArgumentException` if called twice for the same factory (config should be idempotent), and the **static** dictionary keyed by strong `MemoFactory` references is never pruned → every factory is pinned for the app lifetime.

**Fix:** `ConditionalWeakTable<MemoFactory, SynchronizationContext>` + `TryAdd`/indexer.

---

## 🟡 Medium

### M1 — Dead invariant guard in `RequestExclusiveLockAsync`; the real path throws "Should never happen!"
`MemoizR.StructuredAsyncLock/AsyncAsymmetricLock.cs:77-80` & `99-102` *(corrected vs. the automated synthesis)*

`locksHeld` is never negative (the class comment at `AsyncAsymmetricLock.cs:22` describes an unimplemented design — upgradeable uses the separate `upgradedLocksHeld`). So the guard `LocksHeld < 0` is **dead**, and acquiring an exclusive lock while holding an upgradeable one in the same scope falls through to the `else { throw new InvalidOperationException("Should never happen!"); }` — the user gets a nonsense message instead of the intended *"Can not aquire recursive exclusive lock in the scope of an upgradeable lock."* The `ExclusiveLock_BlockedByUpgradeable` test passes via the wrong branch.

**Fix:** drop the dead guard; make the `else` the descriptive throw (or enqueue, if that state should wait). *(Independently verified.)*

### M2 — Async-lock cancellation is entirely dead-wired
`MemoizR.StructuredAsyncLock/Nito/AsyncWaitQueue.cs:63`

The only cancellation-aware `Enqueue` overload (which registers `TryCancel`) is **never called** — the lock uses the raw `Enqueue(double)` at `AsyncAsymmetricLock.cs:97/146`. Consequently `TryCancel`/`CancelAll`/`DequeueAll` are unreachable. **A queued lock waiter can never be cancelled and there is no timeout** — if `ReleaseWaiters` ever fails to grant, the waiter hangs forever. The same overload also declares `int lockScope` while the queue stores `double` (latent precision trap).

**Fix:** wire the cancellation-aware enqueue (or wrap waiter tasks with the token); fix the `int`→`double`. *(Independently verified.)*

### M3 — `ReactionBase` debounce is defeated; continuation runs in a disconnected scope
`MemoizR.Reactive/ReactionBase.cs:200-229`

`Task.Delay(debounceTime, cts.Token).ContinueWith(async _ => …)` has no continuation options, so it runs **even when the antecedent was cancelled** → cancelling `cts` on a rapid re-`Stale` does *not* suppress the update; debounce collapses. AsyncLocal also doesn't flow into the continuation, so it mints a fresh scope and only cleans that one (feeding C1's leak).

**Fix:** `OnlyOnRanToCompletion` (or check `IsCompletedSuccessfully`), `TaskScheduler.Default`, and capture the parent scope id; better, rewrite as `await Task.Delay(...)` in an async method.

### M4 — `ConcurrentMap` never prunes observer links on re-evaluation
`MemoizR.StructuredConcurrency/ConcurrentMap.cs:106-153` *(reframed vs. the automated synthesis)*

`ConcurrentMap` *does* register observers and set `Sources` (via `StructuredResultsJob.HandleSubscriptions` + task body) — it is **not** detached. But unlike `MemoizR`/`ConcurrentMapReduce`, it defines and calls **no** `RemoveParentObservers`, so on re-evaluation the old sources keep observer links to it → memory retention + spurious invalidations from sources it no longer reads, and `Sources` is overwritten wholesale rather than merged.

**Fix:** add a `RemoveParentObservers` pass before the job runs.

### M5 — `StructuredResourceGroup` swallows all disposal exceptions, even on success
`MemoizR.StructuredConcurrency/StructuredResourceGroup.cs:31-62`

The empty `catch` is unconditional, so a resource that fails to dispose **on the success path** (file handle, DB connection) loses its error silently.

**Fix:** collect exceptions, throw an `AggregateException` on the success path; only suppress while already unwinding a primary exception (pass a flag).

### M6 — Reduce seed is `default(T)` with no identity contract
`MemoizR.StructuredConcurrency/StructuredReduceJob.cs:33` (seed `StructuredJobBase.cs:8`)

`result = reduce(r, result!)` starts from `default(T)`. Fine for the default sum (0 is identity), **wrong** for any non-identity reduce — a product folds `×0 → 0`.

**Fix:** seed from the first element, accept an explicit seed, or document the identity requirement.

### M7 — Reduce delegate signature mismatch (factory vs. implementation)
`StructuredConcurrencyFactory.cs:20,25` (`Func<T,T?,T>`) vs `ConcurrentMapReduce.cs:11` / `StructuredReduceJob.cs:9` (`Func<T,T,T?>`)

Nullability of the accumulator and the return are swapped between the public API and the implementation (compiles only because `T?` annotations erase for unconstrained generics). Muddies null semantics; pairs with M6.

**Fix:** standardize on one shape (e.g. `Func<T, T?, T?>`).

### M8 — `Context.Untrack` re-reads `ReactionScope` three times
`MemoizR/Context.cs:107-119` & `121-133`

Save / null / restore each call the getter separately; since the scope is held by `WeakReference`, a GC + re-create between calls makes save/null/restore hit **different instances**, corrupting reaction tracking.

**Fix:** `var scope = ReactionScope;` once, then operate on `scope`. (Largely mitigated once C1 stabilizes identity, but caching is the direct fix.)

### M9 — Dead `WeakReference` entries in `Observers` are never pruned in steady state
`MemoizR/MemoizR.cs:167-173/208-214`

`Observers` is only compacted in `RemoveParentObservers` (relink/dispose); collected observers leave dead `WeakReference`s that every `Stale`/propagation walks forever.

**Fix:** prune opportunistically during iteration or append.

### M10 — `MemoFactory.CONTEXTS` retains dead string keys
`MemoizR/MemoFactory.cs:7` (`CleanUpContexts` at `MemoFactory.cs:47-66` is never called anywhere)

Values are weak, but keys and dead wrappers are never auto-removed; apps creating many named contexts grow it without bound.

**Fix:** prune on lookup/construction, or have `Context` remove its own key on disposal.

### M11 — `StructuredRaceJob` silently swallows losers' exceptions once `finished`
`MemoizR.StructuredConcurrency/StructuredRaceJob.cs:42-55`

Intentional "first wins," but a genuine non-cancellation failure in a loser masked as cancellation is lost; compounded by the H4 stale read.

**Fix:** capture losers' exceptions for optional logging; make the `finished` check barrier-safe.

---

## ⚪ Low

### L1 — Inverted variable name `hasCurrentGets`
`MemoizR/Context.cs:78` — it is `true` when `CurrentGets` is **empty**. Logic is correct; rename to `isCurrentGetsEmpty` to prevent a future inversion bug. Also the two `CS1998` test warnings in `MemoizR.Tests/ResourceManagementTests.cs:82,101`.

---

## Open design questions (need maintainer intent, not just a fix)

1. **Threading model.** Is cross-thread `Set`/`Get` safety actually a goal (the README implies yes)? If so C1+H2+H3 are blocking; the per-scope `ContextLock` design can't deliver a *global* graph lock. If MemoizR is meant to be single-writer, that should be documented and the README claim softened.
2. **`ConcurrentMap`/`ConcurrentRace` reactivity.** Are these meant to be reactive (re-invalidated by source changes) or one-shot? H6/M4 are bugs only under the reactive interpretation.
3. **GC-time cancellation.** Does any code path *rely* on the finalizers (H8)? If not, delete them.
4. **Reduce contract.** Support non-identity reducers + explicit seed (M6/M7), or document the identity requirement?

---

## Suggested remediation order

1. **C1** (fix the scope/lock identity + leak) — it's the root the lock-based fixes for H2/H3 depend on.
2. **C2** (stop swallowing upstream faults) — silent wrong results.
3. **H1, H4, H5, H6, H7** (structured-concurrency correctness).
4. **H8, H9** then the medium leaks/robustness.
5. Add **concurrency stress tests** — these races are intermittent and today's tests pass *around* them. Gate any fix on a high-iteration parallel `Set`/`Get` test plus a faulting-source test for C2.

---

## Verification caveats

- Concurrency findings are static-analysis confirmed but **not demonstrated at runtime**. The data races (Observers/Sources, State visibility, `finished` flag, `result` field, shared `ReactionScope` in reduce) are intermittent by nature — validate with high-iteration parallel/stress tests.
- No test run was performed as part of this review (build only). Run the full `MemoizR.Tests` suite after any fix, especially to confirm the C1 scope-key change does not regress existing behavior.
- Memory-leak findings (AsyncReactionScopes, CONTEXTS, SynchronizationContexts, dead observer refs) were confirmed structurally but not quantified; profile a long-running/high-churn scenario to prioritize among them.
