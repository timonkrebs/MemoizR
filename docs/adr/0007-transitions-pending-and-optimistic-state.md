# ADR 0007 — Transitions, `IsPending`, and optimistic state with automatic rollback

- Status: Proposed (phases 1–4 implemented on this branch — phase 4 without the sample app and
  bUnit component tests; phase 5 open)
- Date: 2026-07-15
- Deciders: MemoizR maintainers
- Inspiration: Solid 2.0's async architecture (deferred stabilization, `isPending`,
  `createOptimistic`, generator actions), re-derived for a pull-based, multi-threaded graph
- Builds on: [ADR 0002](0002-choosing-a-lock.md) (the lock layers this must not re-enter),
  [ADR 0005](0005-custom-executors.md) (the executor seam UI integration rides on),
  [Concurrency Architecture](../architecture/concurrency.md) (the cache-state protocol all
  hooks attach to)

## Context

MemoizR's propagation core is the same design family as Solid 2.0's: writes push staleness
marks without recomputing, reads pull stabilization lazily, reactions are deferred and
debounced, equal-value writes short-circuit. What MemoizR lacks is Solid 2.0's **process
layer for user interfaces**:

1. **Transitions / `isPending`** — an observable "a stabilization is in flight" state, so a
   UI can keep showing the previous committed view (no teardown, no spinner-blanking) while
   indicating background work.
2. **Optimistic state** — a write primitive that instantly projects an expected future value
   onto readers, reconciles with the confirmed value when the backing process completes, and
   **rolls back automatically** when it fails — with no manual recovery logic at the call
   site.

The target surfaces are Blazor first (Server and WebAssembly) and WPF second; both already
have an integration seam in `IExecutor` (ADR 0005).

### Why Solid's primitives cannot be copied 1:1

Three structural differences force a re-derivation rather than a port:

- **Pull-based laziness.** In Solid, a transition is "in flight" from write to scheduler
  flush — the scheduler is global and eager-ish, so *the graph itself* has a notion of
  unfinished work. In MemoizR, a lazy memo that nobody pulls has **no** in-flight work; there
  is nothing pending until a puller exists. The only nodes with autonomous, observable
  latency are **reactions** (debounce window + recompute duration). Therefore pending state
  must anchor to *reactions and explicit transitions*, never to lazy memos alone.
- **True concurrency.** A pending flag flipped from inside evaluation windows would have to
  `Set` a signal while holding the flow's `ContextLock` — exactly the
  exclusive-inside-upgradeable re-entrance the lock rejects (ADR 0002). All runtime-driven
  publications must ride the detached-flow pattern `ReactionBase.RunDebouncedUpdateAsync`
  already uses (`ForceNewScope` on a `Task.Run` flow). And because detached flips can land
  out of order, pending must be a **counter crossing zero**, never a boolean.
- **The process layer already exists.** Solid needed generator actions because JavaScript
  loses the tracking context at `await`; .NET's `ExecutionContext` flows `AsyncLocal` state
  across awaits natively. MemoizR additionally already has the process-side machinery Solid
  had to invent: structured concurrency jobs with cancellation trees and resource groups.
  The "action" primitive below is deliberately a thin composition of `Set` + a structured
  job + a transition — not a new execution model.

## Decision (proposed design)

### A. Transitions: `BeginTransition` and the stabilization wavefront

A **transition** is the unit "this write (or group of writes), until every reaction it
invalidated has committed clean again."

```csharp
var t = f.BeginTransition();
await v1.Set(5);                  // Sets inside the scope are tagged with the transition
await v2.Set("x");
t.Dispose();                      // seals the wavefront (or: await using (t) { ...Sets... })

// observable state:
t.IsPending          // bool snapshot (sync-readable, e.g. from a render)
t.Pending            // IStateGetR<bool> — reactive, other nodes can depend on it
await t.Settled;     // completes once sealed AND the wavefront stabilized (onSettled analog);
                     // reached-reaction faults surface here as an AggregateException
```

Settlement requires the seal (only sealing fixes the wavefront — early drain between two Sets
must not settle), so `Settled` is awaited AFTER the scope: an `await using` block seals at the
block end and additionally awaits settlement itself.

**Mechanics** (all hooks exist today):

1. *Tagging.* `BeginTransition` sets an ambient `AsyncLocal<Transition?>` — the exact pattern
   of `RaceBranchFlow` (`Context.cs`). `Signal.Set` → `PropagateStaleToObserversAsync` →
   `IMemoizR.Stale` runs on the same async flow, so the tag arrives at every reached node
   with zero new plumbing.
2. *Registration.* In `ReactionBase.Stale` (under `staleLock`, where the debounced update is
   scheduled), a tagged invalidation registers `(reaction, transition)` — deduplicated per
   reaction, because coalesced staleness commits once.
3. *Completion.* A reaction notifies its registered transitions when its update **commits
   clean** — the `stateCell.TryCommitClean(token)` success at the end of
   `ReactionBase.UpdateIfNecessary`/`Update`. A refused commit (newer invalidation won) is
   *not* completion; the rescheduled update completes it later. This is monotone and safe:
   the reaction then reflects at-least-as-new state as the tagged write.
4. *Termination edge cases*, each pinned by a test:
   - **Fault**: an `Execute` that throws completes the transition as faulted, carrying the
     exception (structured-concurrency-style error surfacing). The reaction stays Dirty per
     its existing contract.
   - **Dispose**: a disposed reaction completes its transitions (the wavefront can no longer
     stabilize through it).
   - **Pause**: a paused reaction keeps the transition pending until `Resume()` commits —
     honest, but a documented footgun (a paused UI keeps `IsPending` true).
   - **Memo-only cascades**: if propagation reaches no reaction, the transition completes
     when propagation finishes — lazy semantics, documented. An opt-in
     `TransitionOptions.Stabilize` variant may additionally pull every reached memo
     (turning lazy into eager for that one write); deferred until a use case demands it.

The per-context `EnterEvaluationScope`/`ExitEvaluationScope` refcount is the model for the
transition's internal counter; the counter's zero-crossing publishes `Pending` via a detached
runtime flow.

### B. `Reaction.IsPending` — the per-reaction pending indicator

The finest-grained, cheapest primitive, and the one Blazor/WPF bindings will actually use:

```csharp
var save = f.BuildReaction().CreateReaction(m1, v => viewModel.Value = v);
save.IsPending   // IStateGetR<bool>: true from "a Stale scheduled an update"
                 // until "that update committed clean"
```

Protocol: an `int pendingCount` on `ReactionBase`, incremented in `Stale` when an update is
scheduled, decremented on the superseded-early-return path of `RunDebouncedUpdateAsync` and
on clean commit. The boolean projection `count > 0` is published on zero-crossings through a
detached flow into an internal leaf signal. Supersession storms therefore keep
`IsPending == true` continuously (correct: work *is* in flight) instead of flickering.

Because `IsPending` is itself a graph node, "show a progress bar while revalidating, keep the
current view" is just another reaction — Solid's `isPending`-preserves-the-DOM behavior falls
out of the composition rather than needing framework support.

A convenience `f.CreatePendingIndicator(params ...)` OR-ing several nodes' pending states can
be layered later; it needs the same internal stabilization notification and adds no new
semantics.

### C. Optimistic state: overlay composition, structural rollback

The optimistic primitive is deliberately **not** a new node type in the propagation core. It
is a composition of existing primitives, chosen so that rollback is *structural* (remove an
overlay) instead of *compensating* (write the old value back) — a compensating write races
concurrent writers and can resurrect stale state; removing an overlay cannot.

```csharp
// base: the confirmed truth — an atomically updatable source, or a memo over a fetch
var todos     = f.CreateEagerRelativeSignal(ImmutableList<Todo>.Empty);

// optimistic view = base + pending patches, in one node
var optimistic = f.CreateOptimistic<ImmutableList<Todo>>(todos);

var addTodo = f.CreateAction<Todo>(async (todo, ctx) =>
{
    await ctx.Apply(optimistic, list => list.Add(todo with { Pending = true })); // instant projection
    var confirmed = await api.SaveAsync(todo, ctx.Token);                        // the process step
    await todos.Set(list => list.Add(confirmed));  // confirm atomically: overlapping runs compose
});                       // run end drops the patches: on fault/cancel that IS the rollback

var run = addTodo.Run(newTodo);  // detached; run.Completion is the body, run.Settled the effects
addTodo.IsPending                // reactive pending indicator across all runs
await run.Settled;               // the UI reflects the final outcome (patch gone)
```

**Internals:**

- `CreateOptimistic(base)` creates an internal `Signal<ImmutableList<Patch<T>>>` (the
  overlay) plus a `MemoizR<T>` computing `Apply(await base.Get(), await overlay.Get())`.
  Everything — instant projection, propagation, memoization, evidence — is the existing
  machinery; strict mode's Sendable checking covers patch payloads unchanged.
- `CreateAction` wraps the body in a **structured-concurrency job** (cancellation token,
  resource group — this is MemoizR's analog of Solid's generator actions) and a
  **transition** (part A). Its guarantees:
  - *Apply* pushes a keyed patch onto the overlay signal — one ordinary `Set`.
  - *Confirm* writes the confirmed value to the base **and removes the patch**. The two
    `Set`s land within one debounce window, so reactions coalesce them into a single update
    and never render the intermediate frame; direct concurrent `Get`s can observe it, which
    is acceptable for UI state and honestly stamped (each publication carries its own
    causality evidence). A later `WriteBatch` API (one exclusive acquisition, one combined
    propagation) can make the pair atomic for non-UI consumers — explicitly out of scope
    here.
  - *Rollback* is the `catch`/cancellation path: remove the patch, nothing else. The base
    was never touched, so there is nothing to compensate. Overlapping actions compose: each
    owns its patch; a failed action's rollback cannot disturb a concurrent action's
    projection.
- **Refresh/reconcile** for memo-backed bases needs one small core addition: a public
  `Invalidate()` on `MemoBase<T>` (force `CacheDirty` + propagate `Stale`, modeled on
  `Signal.Set`'s locking) — the analog of Solid's `refresh(todos)`. This is independently
  useful (cache-expiry, server-push invalidation) and is the only change the propagation
  core needs for part C.

The causality stamps (issue #39) tie in naturally: a patch can capture the base stamp it was
projected against, which a distributed reconciler can later use to detect that the server
truth moved under an optimistic projection. Not in scope here, but the overlay design keeps
that door open where a compensating-write design would close it.

### D. Blazor and WPF integration

**New package `MemoizR.Blazor`** (mirroring `MemoizR.Wpf`'s size and shape):

- `BlazorDispatcherExecutor : IExecutor` — the exact mirror of `WpfDispatcherExecutor`:
  `Enqueue → Dispatcher.InvokeAsync`, `IsCurrent → Dispatcher.CheckAccess()`. Reaction
  dependency evaluation stays on the thread pool; only the action (typically
  `StateHasChanged`) runs on the renderer's dispatcher — the #13 contract, unchanged.
- `services.AddMemoizR()` — a **scoped** `MemoFactory` per circuit (Blazor Server) /
  per app (WASM). Blazor Server circuits are genuinely multi-threaded, which is exactly
  where MemoizR's cross-flow correctness (generation guards, tear-free boxes) pays off over
  single-threaded signal ports.
- `MemoizRComponentBase` (or a standalone `ReactionBinder` for people who cannot change base
  class): `Bind(memo, v => field = v)` builds a `Reaction` via the existing
  `ReactionBuilder.CreateReaction(dep, action)` overloads whose action stores the value and
  calls `InvokeAsync(StateHasChanged)`; all binders are disposed with the component
  (prerender-then-dispose safe). Because `BuildRenderTree` is synchronous, markup reads the
  binder's cached snapshot — including cached `IsPending`/`Transition.IsPending` booleans:

  ```razor
  <button disabled="@addTodo.IsPendingSnapshot" @onclick="() => addTodo.Run(newTodo)">Add</button>
  @foreach (var todo in _todos) { ... }   @* optimistic view, pending items styled muted *@
  ```

**WPF** gets parts A–C for free through the existing executor seam. One optional addition:
`BindableValue<T>` — an `INotifyPropertyChanged` wrapper backed by a reaction — so optimistic
views and pending flags bind directly in XAML without hand-written reactions.

## Implementation plan

Phases are ordered so each ships alone and each unlocks the next; nothing lands in the
propagation core without a Coyote story.

| Phase | Deliverable | Key touch points | Tests |
|---|---|---|---|
| **1. Core instrumentation** | Internal *stabilization notification* (the missing "committed clean" half of the observer protocol, internal-only); `MemoBase<T>.Invalidate()`; ambient transition tagging (`AsyncLocal`, `RaceBranchFlow` pattern) | `CacheStateCell`, `SignalHandlR.CommitCleanOrRenotifyAsync`, `MemoBase`, `Context` | Coyote interleavings for notify-vs-invalidate races; `Invalidate()` storm tests alongside `StormTests` |
| **2. Transitions + `IsPending`** (`MemoizR.Reactive`) | `Transition` (`IsPending`/`Pending`/`Settled`/`Exception`), `MemoFactory.BeginTransition()`, `ReactionBase.pendingCount` + `Reaction.IsPending` | `ReactionBase.Stale`, `RunDebouncedUpdateAsync`, `UpdateIfNecessary` commit sites | `FakeTimeProvider` debounce tests (supersession keeps pending true; zero-crossing publishes once); fault/dispose/pause edge cases; Coyote on the counter |
| **3. Optimistic state** | `CreateOptimistic`, `Patch` overlay, `CreateAction` (structured job + transition + rollback) | New files in `MemoizR.Reactive` (or a new `MemoizR.Optimistic`); no core changes beyond phase 1 | Lifecycle table from the Solid PDF as a test matrix: initial → apply → in-flight → confirm → rollback; overlapping actions; cancellation; strict-mode Sendable patches |
| **4. UI packages** | `MemoizR.Blazor` (executor, DI, `MemoizRComponentBase`), optimistic-todo sample (Server + WASM); WPF sample update, optional `BindableValue<T>` | New project; mirrors `MemoizR.Wpf` | bUnit component tests (render coalescing, dispose-during-prerender, `IsPending` snapshot flips); manual sample validation |
| **5. Hardening & docs** | README section, architecture doc chapter, analyzer follow-ups (e.g. flag non-pure patch functions, `Set` outside actions on optimistic bases), `WriteBatch` decision | `MemoizR.Analyzers`, docs | Analyzer golden tests |

Suggested spike order inside phase 1: stabilization notification first — it is the one
mechanism parts A, B, and a future `CreatePendingIndicator` all stand on, and the one with
real concurrency risk (it must fire outside the locks it is called under, on the detached
runtime flow, without reordering against a newer invalidation).

### Implementation notes (phases 1–3, this branch)

Where the landed code deliberately deviates from the sketches above:

- The stabilization notification carries the committed generation token; consumers compare it
  against the invalidation's post-bump generation (`Invalidate(state, out generationAfter)`),
  with a `LastCleanCommitToken` recovery read that makes registration-vs-commit ordering free.
  A faulted reaction update notifies `OnStabilizationFaulted`; a disposed reaction releases
  its waiters (`int.MaxValue` satisfies every threshold).
- The ambient tag (`TransitionFlow`) lives in `MemoizR.Reactive`, not core: both its writer
  (`BeginTransition` / action runs) and its reader (`ReactionBase.Stale`) are in the Reactive
  assembly. Detached runtime flows — debounced updates, pending-publish pumps — suppress the
  inherited tag, otherwise a transition's own machinery Stales (renotifies, pending-signal
  propagation) would re-arm it forever.
- The action sketch's explicit `Confirm` step was dropped: a run's patches are removed
  unconditionally when the body ends, so "confirm" is simply the body writing the source of
  truth before returning — one less concept, same lifecycle table.
- `CreateAction` uses a plain per-run `CancellationTokenSource` instead of depending on
  `MemoizR.StructuredConcurrency`; an overload wrapping a structured resource group can be
  layered in that assembly later without touching the Reactive types.
- A tagged write's cascade does NOT prune at already-dirty nodes (core `WavefrontFlow`, an
  opaque ambient the Reactive tag rides in): the pruning optimization is sound for
  invalidation but would hide the write from downstream registrations — a transition through
  an already-dirty memo could settle before the write's effects applied, and repeated writes
  could not raise registered thresholds. Untagged writes keep the pruned cascade. Fault
  notifications carry the faulted update's generation token and are gated by the same
  threshold rule as commits, so an old in-flight failure cannot fault a newer wavefront whose
  superseding update is still owed a commit.
- Known strict-mode gap, deferred to the phase-5 analyzers: optimistic patch DELEGATES are not
  runtime-validated. A patch runs inside the view's computation on whichever flow pulls, so a
  lambda closing over mutable state crosses flows — but closure display classes always carry
  writable fields, so a structural runtime check would reject every capturing lambda, including
  harmless immutable captures. This is exactly MZR002-shaped compile-time work: an analyzer
  rule flagging optimistic patches that capture mutable state (the payload type itself IS
  checked — `CreateAction` gates `TPayload` through the strict Sendable bar).
- Phase 4 landed as `MemoizR.Blazor`: `BlazorDispatcherExecutor` (the exact
  `WpfDispatcherExecutor` mirror), a component-agnostic `ReactionBinder` (the render-snapshot
  pattern: apply-into-field plus `StateHasChanged`, both marshalled through `InvokeAsync`),
  `MemoizRComponentBase` gluing it to components, and `services.AddMemoizR()` for the
  scoped-per-circuit factory. Integration-tested against `Dispatcher.CreateDefault()` — the
  renderer dispatcher Blazor Server uses — so the thread-pool/renderer split is pinned without
  a renderer; the optimistic-todo sample app and bUnit component tests remain open.

## Consequences

- The propagation core stays untouched except for two additive, independently useful
  mechanisms (stabilization notification, `Invalidate()`); everything UI-facing composes on
  top. The actor engine (ADR 0006) can adopt the same notification in a later pass —
  transitions/optimism are lock-engine-first, like every other feature.
- Pending semantics are honest about laziness: they describe reactions and transitions, and
  the docs must say so (a lazy memo nobody observes is never "pending").
- Optimistic rollback is correct by construction (overlay removal), at the cost of one extra
  node + one list signal per optimistic view — negligible against the recompute it fronts.
- The debounce window doubles as the write-batching mechanism; the `WriteBatch` question is
  deferred, not decided.

## Alternatives considered

- **Boolean pending flag flipped in place** — rejected: detached publications reorder; a
  counter with zero-crossing publication is race-free by construction.
- **Compensating-write rollback** (snapshot old value, `Set` it back on failure) — rejected:
  races concurrent writers, resurrects stale state, and cannot compose across overlapping
  actions. Overlay removal has none of these failure modes.
- **A `PendingSignal` node type inside the core** — rejected: it would push UI semantics into
  the propagation engine and require `Set`-under-evaluation, which the lock architecture
  forbids for good reasons (ADR 0002).
- **Auto-tracking Blazor renders** (dependency capture inside `BuildRenderTree`) — deferred:
  renders are synchronous, MemoizR reads are async; the snapshot-binder pattern covers the
  need without a sync-read escape hatch into the graph.
