# Code Review — PR #108 "Add coyote testing" (`8f272bb`, diff vs `HEAD~1`)

> **Status (2026-06-10): all 16 steps fixed** in the working tree. Both new regression tests
> (`Memo_SuppressedStaleDuringParentCheck_IsNotClobbered`,
> `Reaction_ResumeInsideActiveEvaluation_DoesNotDestroyEnclosingScope`) were verified to fail
> with the fixes reverted and pass with them applied. Full suite 60/60 (3 consecutive runs),
> Coyote systematic test green on rewritten assemblies, cognitive-complexity gate green.

Max-effort review of the CacheStateCell generation guard, ValueBox publication,
Resume locking, volatile rationalization, extract-method refactors, tests and CI
wiring. Each finding below is a self-contained step that can be fixed and
verified individually. Steps are grouped by phase; within a phase they are
independent unless an **Ordering** note says otherwise.

**Verification status legend:**
- `CONFIRMED` — the failing interleaving/input was constructed and checked against the code.
- `PLAUSIBLE` — mechanism is real; the trigger depends on timing/environment.

**Refuted during review (no action needed):**
- `ConcurrentRace`'s exemption from CacheStateCell is sound — its `Get` always locks and always recomputes; it has no Clean fast path.
- The globalconfig precedence is correct: `dotnet_diagnostic.S3776.severity` (rule-id) beats `dotnet_analyzer_diagnostic.severity = none`, so the CI gate does fire.
- The new `cognitive-complexity` job inherits the workflow-level `permissions: contents: read`.
- The ValueBox release/acquire pairing with the volatile `State` read is memory-model sound as ADR 0001 argues.

Also noticed: the untracked `dotnet/` directory in the working tree is a build
artifact (`runfile-discovery/.../cache.staging.json`) — consider adding it to
`.gitignore` (see Step 16).

---

## Phase 1 — Merge-blocking correctness

### Step 1 — Bump the generation on every `Invalidate`, even when suppressed

- **Severity:** Critical · **Status:** CONFIRMED (two independent traces agree)
- **Where:** `MemoizR/CacheStateCell.cs:44`

**Problem.** `Invalidate()` returns `false` **without bumping the generation**
when the node is already at least as dirty (`newState <= state`). A suppressed
`Stale` therefore leaves no trace, and an in-flight evaluation's
`TryCommitClean` succeeds over a pending dirty parent — the exact cross-flow
lost-update class the cell was added to close.

**Failure scenario.** Graph `s -> p (memo) -> c (memo or reaction)`:
1. Set#1 leaves `p` recomputed and `c` resolving `CacheCheck`; `c` runs
   `UpdateIfNecessary` (token snapshotted, state `CacheCheck`), checks `p`
   (unchanged).
2. Set#2 lands mid-check: `p.Stale(CacheDirty)` bumps `p` and propagates
   `c.Stale(CacheCheck)` — but `c` is already `CacheCheck`, so `Invalidate`
   returns `false` with **no generation bump** (and propagation to `c`'s own
   observers is also skipped).
3. `c.TryCommitClean(token)` succeeds → `c` is `CacheClean` while `p` is
   `CacheDirty` with a newer value. Every later `c.Get()` takes the lock-free
   fast path and returns the stale value.
4. **A third Set does not recover it:** `p.Stale` early-returns (`p` already
   Dirty) and never re-notifies `c`. For a `ReactionBase`, the rescheduled
   debounced update sees `CacheClean` and returns — a permanently missed
   trigger.

**Suggested fix.** Bump `generation` in `Invalidate` even when the state does
not escalate (i.e. before the `newState <= state` early return). Keep the
`bool` return for the propagation-skip if desired, but the bump must be
unconditional. Re-examine whether the propagation skip itself is safe once the
bump is unconditional.

**Verify.** Add a deterministic regression test in the style of
`Memo_StaleDuringRecompute_IsNotClobbered`, but park the child in its
**CacheCheck parent-scan** (not `Evaluating`) and deliver a second `Set` whose
cascade reaches the child as `Stale(CacheCheck)` while it is already
`CacheCheck`. Assert the child reconverges. Run the full suite + Coyote.

---

### Step 2 — Make `Resume()` (and the debounce path) clean up only scopes it created

- **Severity:** Critical · **Status:** CONFIRMED (mechanism verified against `Context.cs`)
- **Where:** `MemoizR.Reactive/ReactionBase.cs:51` (Resume), same pattern at `ReactionBase.cs:291` (RunDebouncedUpdateAsync, pre-existing)

**Problem.** `Resume()`'s `finally` calls `Context.CleanScope()`
unconditionally, even when `CreateNewScopeIfNeeded()` created nothing.
`CleanScope` removes `AsyncReactionScopes[AsyncLocalScope.Value]` — the live
scope of whatever outer evaluation pinned it. `AsyncLocalScope.Value` stays
set, so the enclosing frame's next `Context.ReactionScope` access silently
mints a **fresh empty scope** (new `ContextLock`, `CurrentGets = []`,
`CurrentReaction = null`).

**Failure scenario.** `Resume()` called from code already running in a pinned
scope (e.g. inside a memo fn or reaction body): the enclosing `Update` resumes
on the new empty scope, its `UpdateSourceAndObserverLinks` sees
`CurrentGets.Length == 0` with index 0, truncates `Sources`, and unsubscribes
the node from all parents — it never reacts or recomputes again. The same
unconditional pattern in `RunDebouncedUpdateAsync` lets two reactions debounced
from one Set flow tear down each other's shared scope mid-evaluation.

**Suggested fix.** Make `CreateNewScopeIfNeeded()` return whether it created a
scope (or return a disposable token) and call `CleanScope()` only in that case.
Apply to both `Resume()` and `RunDebouncedUpdateAsync`. Audit the other
`CreateNewScopeIfNeeded` call sites (`MemoizR.Get`, `Signal.Get`, etc. never
clean — document why, or unify).

**Verify.** New test: call `r.Resume()` from inside a memo's `fn` (or a
reaction body) and assert the enclosing memo still tracks its sources
afterwards (a later `Set` retriggers it). Second test: two reactions debounced
from one `Set`, assert both keep reacting.

---

### Step 3 — Fix the double-completion crash in `InvokeExecute`'s callback

- **Severity:** Critical (for SynchronizationContext consumers) · **Status:** CONFIRMED
- **Where:** `MemoizR.Reactive/ReactionBase.cs:180`

**Problem.** Inside `InvokeExecute`'s `async void SendOrPostCallback`, when
`Execute()` throws, the `catch` runs `tcs.SetException(e)` and control then
falls through to the unconditional `tcs.SetResult()`. `SetResult` on an
already-faulted TCS throws `InvalidOperationException` **inside an async
void** posted to the SynchronizationContext → unhandled → process/UI crash,
even though the awaiting `Update` would have propagated the original exception
correctly. (Code is pre-existing but was relocated verbatim into the new
method by this commit — this refactor was the chance to fix it.)

**Suggested fix.** `return;` after `tcs.SetException(e)`, or move
`tcs.SetResult()` to the end of the `try` block. Consider `TrySet*` variants
for belt-and-braces.

**Verify.** Test: reaction constructed with a `SynchronizationContext` whose
`Execute` throws; assert the exception surfaces via the update path and no
unhandled exception is raised on the context.

---

## Phase 2 — Correctness gaps and ineffective fixes

### Step 4 — `Resume()`'s lock does not serialize against cross-flow Sets; same-flow body-Set now throws

- **Severity:** High · **Status:** CONFIRMED by inspection (ineffectiveness); behavioral regression for body-Set
- **Where:** `MemoizR.Reactive/ReactionBase.cs:44`

**Problem.** The new `UpgradeableLockAsync` acquires the **per-flow**
`ContextLock`. A concurrent `Signal.Set` on another flow (e.g. from
`Task.Run`, where `AsyncLocalScope == 0`) resolves a brand-new throwaway
`ReactionScope` and locks a lock no one else references — so the
Sources/Observers rewiring race the commit message claims fixed is still
unserialized. Additionally, `Resume` now holds the upgradeable lock while
`Execute` runs, so a reaction body that calls `await someSignal.Set(x)` on the
same flow hits the exclusive-inside-upgradeable rejection and surfaces
`InvalidOperationException("Should never happen!")` where the old lock-free
`Resume` succeeded.

**Suggested fix.** Decide what `Resume` must actually be serialized against
and implement that: graph mutation safety is provided by the
`SignalHandlR.Lock` interface setters + whole-array swaps (per ADR 0002 §3) —
if that is the real guarantee, simplify `Resume` and correct its comment; if
cross-flow serialization is genuinely needed, it requires a shared (per-node
or per-context) mechanism, not the per-flow `ContextLock`. Document the
body-calls-Set behavior either way (it already throws on the debounced path —
see Step 5's interaction).

**Verify.** Test that `Resume` with a body performing `Set` either works or
fails with a *documented, intentional* exception; stress test Resume vs
cross-flow Sets asserting no torn observer rewiring (no lost subscriptions).

---

### Step 5 — Concurrent debounced updates are unserialized; stale side effects can land last

- **Severity:** High · **Status:** PLAUSIBLE (mechanism verified; window is real but narrow)
- **Where:** `MemoizR.Reactive/ReactionBase.cs:283`; flaky test at `MemoizR.Tests/ReactiveTests.cs:472`

**Problem.** Two debounced updates of the same reaction can run concurrently:
each inherits a different flow → different `ContextLock`, and the
`UpdateIfNecessary` path takes no node mutex. The generation guard protects
the **State commit**, not `Execute`'s **side-effect ordering**. `cts.Cancel()`
only cancels updates still inside `Task.Delay`.

**Failure scenario.** `Set(k)` schedules U1; U1 passes the delay, reads
`v1 = k`, is descheduled before running the action. `Set(1000)` bumps the
generation and schedules U2 on another flow; U2 runs fully (`last = 1000`,
commit Clean succeeds). U1 resumes and assigns `last = k`; its commit fails
but state is already Clean, so nothing re-runs → the reaction permanently
shows `k` while the signal is 1000. The new
`Reaction_ResumeConcurrentWithSet_DoesNotThrowAndConverges` test asserts
convergence under exactly this race and will flake on CI.

**Suggested fix.** Serialize a reaction's update execution per node (e.g. the
inherited `SignalHandlR.mutex`, or an internal sequential work loop that only
ever runs the latest scheduled update). If deferred, mark the test's
convergence assertion accordingly and track the gap.

**Ordering:** fix after Step 1 (both touch the reaction update path; Step 1
changes when commits fail).

**Verify.** Deterministic test with two gated updates forcing the
stale-side-effect-last interleaving; the existing convergence test should then
be stable across repeated runs (`dotnet test --filter ResumeConcurrentWithSet` in a loop).

---

### Step 6 — Restore a visibility guarantee for `CurrentGets`/`CurrentGetsIndex` on the ReduceJob path

- **Severity:** Medium-High · **Status:** PLAUSIBLE (formal memory-model regression; comment factually wrong)
- **Where:** `MemoizR/Context.cs:12`

**Problem.** The volatile removal is justified by "only ever touched while
serialized by the ContextLock", but `StructuredReduceJob` children never
`ForceNewScope`: they share the parent flow's scope, and the per-flow-reentrant
`ContextLock` does **not** serialize them. Writes happen under `Context.Lock`
(`CheckDependenciesTheSame`) while `StructuredReduceJob.ExecuteFn` reads under
the job's different `Lock` — no common monitor, no happens-before edge.

**Failure scenario.** `ConcurrentMapReduce` with two fns reading different
signals: child B's `ExecuteFn` observes a stale `CurrentGets` missing child
A's appended source → that source never lands in `allSources` → the node never
registers as its observer → serves a stale value forever after that signal
changes.

**Suggested fix.** Either restore `volatile` on these two fields (cheapest,
matches ADR rule 2: they *are* read on a path no single lock covers), or make
`ExecuteFn` read them under `Context.Lock` (the writers' monitor), or give
ReduceJob children per-child scopes like ResultsJob. Update the comment and
ADR 0001 rule 1 list to match reality.

**Verify.** Code inspection + ADR update; optionally a Coyote test over a
two-fn `ConcurrentMapReduce` asserting both sources end up observed.

---

### Step 7 — Dead recursion guard in `AsyncAsymmetricLock`; new test cements the accidental path

- **Severity:** Medium · **Status:** CONFIRMED
- **Where:** `MemoizR.StructuredAsyncLock/AsyncAsymmetricLock.cs:79`; test at `MemoizR.Tests/StructuredAsyncLockTests.cs:238`

**Problem.** The "recursive exclusive in the scope of an upgradeable" guard
checks `LocksHeld < 0`, but `locksHeld` never goes negative — upgradeable
acquisition increments `upgradedLocksHeld` (the field comment "negative if
upgradeable lock are held" is stale). With an upgradeable held in the same
scope, an exclusive acquire skips both recursion guards and lands on the
generic `throw new InvalidOperationException("Should never happen!")` at line
102. The new `ExclusiveLock_WhileHoldingUpgradeableInSameScope_Throws` asserts
only the exception **type**, green-lighting the accidental branch.

**Suggested fix.** Change the guard to `UpgradedLocksHeld > 0 && LockScope ==
lockScope` (intended semantics), fix the field comment, and make the test
assert the intended message (like its sibling test does with
`Contains("recursive exclusive", ...)`).

**Verify.** The updated test fails before the guard fix and passes after; full
lock test suite + Coyote.

---

## Phase 3 — Test and CI hardening

### Step 8 — Add `RunContinuationsAsynchronously` to the new tests' TCS gates

- **Severity:** Medium (latent hang) · **Status:** PLAUSIBLE
- **Where:** `MemoizR.Tests/RegressionTests.cs:189` (and the five gates in `ReleasingFlow_DoesNotCorruptScope_WhenHandingOffToWaiter`)

**Problem.** `readDone.SetResult()` / `proceed.SetResult()` run test
continuations synchronously inline inside the memo's `fn` while the getter
flow holds the node mutex, `ContextLock`, and `CancellationTokenSource` state.
It avoids deadlock today only because the test flow's `Set` resolves a
distinct throwaway scope/lock; any change to scope sharing (or a
SynchronizationContext) turns it into a same-stack lock contention and a
10-second timeout hang.

**Fix.** Construct every gate TCS with
`TaskCreationOptions.RunContinuationsAsynchronously`.

---

### Step 9 — Raise (or split) the 2-minute job timeout

- **Severity:** Medium (CI reliability) · **Status:** PLAUSIBLE
- **Where:** `.github/workflows/dotnet.yml:22`

**Problem.** The build/test job keeps `timeout-minutes: 2` across a 4-OS
matrix while this commit adds tests with individual budgets up to 20s, 5s
convergence polls, and two 100-task lock tests — on top of restore, build, and
the Coyote rewrite. On the slow legs (`ubuntu-24.04-arm`, `macos-latest` —
where this PR already observed flakes) the job can hit the ceiling and be
cancelled even though every test passes.

**Fix.** Raise to a realistic budget (e.g. 10 minutes) or split the Coyote
steps into their own job.

---

### Step 10 — Stop the stress-test readers from busy-spinning the thread pool

- **Severity:** Medium (flake source) · **Status:** PLAUSIBLE
- **Where:** `MemoizR.Tests/RegressionTests.cs:110` and `:153`

**Problem.** Both fast-path stress tests spin 8 reader tasks in tight loops
that never yield (the CacheClean fast path completes `Get()` synchronously),
pinning 8 pool threads. On a 2–4 vCPU runner the writer's `Set` continuations
and concurrently running test classes starve, pushing tests toward their 20s
timeouts.

**Fix.** `await Task.Yield()` each iteration, or cap readers at
`Environment.ProcessorCount - 1`.

---

### Step 11 — Pin the SonarAnalyzer version

- **Severity:** Medium (CI determinism) · **Status:** CONFIRMED
- **Where:** `Directory.Build.props:28`

**Problem.** `Version="*"` floats: a new SonarAnalyzer release can change
S3776 scoring or break restore, failing the gate on an unrelated PR and making
CI and local runs disagree.

**Fix.** Pin a known version; let Dependabot bump it deliberately (it already
manages this repo's pins).

---

### Step 12 — Make the fast-path stress assertion falsifiable

- **Severity:** Low (test quality) · **Status:** CONFIRMED
- **Where:** `MemoizR.Tests/RegressionTests.cs:115`

**Problem.** `Assert.True(r % 2 == 0, ...)` is vacuous: `r` is an `int`
computed as `v1 * 2`, and a 32-bit read cannot tear, so the per-read check can
never fail; only the final convergence assert tests anything. The test guards
less than its name and comment claim.

**Fix.** Assert something falsifiable per read (e.g. values are monotonically
non-decreasing snapshots of committed writes), or drop the loop assertion and
correct the comment so coverage isn't overstated.

---

## Phase 4 — Consolidation (after Phase 1–2 land)

### Step 13 — Hoist the duplicated graph-rewiring and state-cell protocol into the base class

- **Severity:** Maintainability · **Status:** CONFIRMED duplication
- **Where:** `MemoizR/MemoizR.cs:162`, `MemoizR.StructuredConcurrency/ConcurrentMapReduce.cs:169`, `MemoizR.Reactive/ReactionBase.cs:194`

**Problem.** `UpdateSourceAndObserverLinks` (plus `RemoveParentObservers`) is
a byte-identical private copy in three classes across three assemblies
(~99 lines), and the `CacheStateCell` wiring (field + comment + `State`
property + `InvalidateFromParent` setter + the 6-call
snapshot/begin/commit protocol) is pasted into four classes, with
`ConcurrentRace` exempted only by argument. All of it operates on shared
`SignalHandlR`/`MemoHandlR` state. This very diff needed four identical hunks
for one logical change; a fifth node type can wire the lost-update guard
subtly wrong.

**Fix.** One protected implementation on `SignalHandlR`/`MemoHandlR`
(`InternalsVisibleTo` already covers the sibling assemblies); consider a
template-method `UpdateCore` so the snapshot/commit choreography exists once.

**Ordering:** do this **after** Steps 1, 2, 4, 5 so the consolidated code is
the corrected code.

---

### Step 14 — Deduplicate `MarkObserversDirty` and the job `ExecuteFn` lock-block

- **Severity:** Maintainability · **Status:** CONFIRMED duplication (already drifted)
- **Where:** `MemoizR.StructuredConcurrency/ConcurrentMap.cs:158`; `StructuredReduceJob.cs:48` vs `StructuredResultsJob.cs:48`

**Problem.** `MarkObserversDirty` was extracted in `MemoizR.cs` and
`ConcurrentMapReduce.cs`, but `ConcurrentMap` keeps the same loop inline with
the old `Observers.Length > 0` guard and broken indentation — three copies of
the diamond down-link rule that already disagree textually. The source-link
`lock (Lock)` block is duplicated verbatim (modulo an `i`/`j` rename) between
the two job classes whose shared base `StructuredJobBase` is the natural home.

**Fix.** Extract `MarkObserversDirty` to the base (folds into Step 13);
extract the job lock-block into a protected `StructuredJobBase` helper. Fix
the `ConcurrentMap` indentation while there.

---

### Step 15 — Share one test convergence-wait helper

- **Severity:** Low · **Status:** CONFIRMED duplication
- **Where:** `MemoizR.Tests/StructuredConcurrencyTests.cs:311` vs hand-rolled polls in `ReactiveTests.cs` and `RegressionTests.cs`

**Problem.** `WaitForConvergenceAsync` and two hand-rolled Stopwatch poll
loops were introduced by the same commit with already-diverging delay
granularities (10ms vs 20ms).

**Fix.** One internal shared test utility (e.g. `TestHelpers.WaitUntilAsync`),
used by all three sites.

---

### Step 16 — Housekeeping

- Add the build artifact `dotnet/` (runfile-discovery cache) to `.gitignore`.
- ADR 0001: after Step 1/6 land, update the "Cross-flow state coordination"
  section (generation bump now unconditional) and rule 1's field list
  (`CurrentGets`/`CurrentGetsIndex` outcome of Step 6).
- `ValueBox` allocation per `Signal.Set` (`MemoizR/MemoHandlR.cs:65`): one
  heap allocation on the library's write hot path, where the box's
  tearing/ordering guarantees matter least (Signal has no Clean fast path
  pairing). Optional: keep the box only on memo nodes, or document the Gen0
  cost as accepted in ADR 0001. — *Status: CONFIRMED cost, deliberate
  trade-off; decide and document.*
