# Change Log
All notable changes to this project will be documented in this file.
 
The format is based on [Keep a Changelog](http://keepachangelog.com/)
and this project adheres to [Semantic Versioning](http://semver.org/).

## [Unreleased]

### Added
- First data-race safety layer for the user boundary (issue #36, see ADR 0003): a `[Sendable]`
  attribute, structural verification via `SendableChecker`, and an opt-in
  `MemoFactoryOptions.StrictSendableChecks` factory mode that rejects, at node creation,
  value types that are not verifiably immutable or thread-safe.
- Dynamic isolation assertions (the runtime analog of Swift's `preconditionIsolated`):
  `AsyncAsymmetricLock.IsHeldByCurrentFlow`, `Context.IsEvaluationIsolated`,
  `MemoFactory.AssertEvaluationIsolated()`, and a DEBUG-only assert that graph rewiring only
  ever happens inside a ContextLock-serialized evaluation.
- Compile-time data-race diagnostics (issue #36 second layer, see ADR 0004): a
  `MemoizR.Analyzers` Roslyn package bundled inside the MemoizR NuGet package — MZR001
  (non-Sendable value type at a factory creation site, mirroring strict mode), MZR002 (a
  reactive computation writes captured/shared state; lift it into a Signal), MZR003
  (`Signal.Set` inside a computation, which throws at runtime, reported at build time).
  Reference types with a non-private settable (non-init) property now count as non-Sendable
  in both the analyzer and `SendableChecker`, and a visible get-only property's type must
  itself be Sendable (with `System.Type` green-listed on both sides so non-sealed records'
  synthesized `EqualityContract` is not falsely rejected). MZR002/MZR003 also analyze
  computations passed as method groups or local functions declared in the same file, MZR002
  flattens nested deconstruction targets (`(a, (b, c)) = ...`), and MZR003 inspects only the
  computation's direct execution path, so a deferred callback the computation merely builds
  (its own documented escape) is not flagged. The collection green-list matches known framework
  definitions rather than namespaces (user types declared inside `System.Collections.*` are not
  blessed), which also makes the deliberately-abstract `FrozenDictionary`/`FrozenSet` verify
  correctly on both sides.
- Custom executors for reactive side effects (issue #36, the Swift SE-0392 analog, see ADR
  0005): `IExecutor` (`Enqueue`/`IsCurrent`) with `executor.AssertIsolated()`,
  `SynchronizationContextExecutor`, a `DedicatedThreadExecutor` whose installed
  SynchronizationContext keeps async continuations on its thread (a true serial isolation
  seat), `MemoFactory.AddExecutor(...)`, and a per-builder `ReactionBuilder.AddExecutor(...)`
  override. Reaction marshalling now routes through `IExecutor`: `AddSynchronizationContext`
  and `MemoizR.Wpf`'s `AddWpfDispatcher` wrap the context in a `SynchronizationContextExecutor`.
- EXPERIMENTAL actor engine (issue #36 layer 5, see ADR 0006): `CreateActorSignal` /
  `CreateActorMemoizR` nodes whose graph bookkeeping runs as synchronous turns of a per-context
  `GraphActor` (a serial channel loop that also implements `IExecutor`) instead of under locks —
  same observable semantics (lazy memoization, generation-guarded commits, diamond absorption,
  dynamic rewiring, lock-free clean fast path), no monitors, no deadlock surface, plus a
  read-evidence guard (`(source, generation)` pairs re-verified at commit) that closes the
  late-wired-observer staleness hole. Failure paths record the caller's read evidence before
  bumping the generation, so a computation that catches a dependency's failure cannot cache its
  fallback Clean over the still-dirty dependency; cycles are detected via the evaluation chain
  (unawaited sibling reads of one memo from a single computation wait instead of throwing);
  cross-context actor reads are rejected at the read; equal-value `ActorSignal.Set` is a
  complete no-op, matching the lock engine.
- `CreateReaction(...)` convenience overloads directly on the factory — sugar for `BuildReaction().CreateReaction(...)` with the default label and debounce
- MemoizR.Wpf package: `AddWpfDispatcher` routes reaction actions to the WPF UI thread (via `Application.Current.Dispatcher` or an explicit `Dispatcher`) while the dependency graph keeps evaluating on the thread pool (#13)

### Changed
- Reactions now evaluate their separate-parameter dependencies in parallel on the thread pool; with an executor registered (e.g. via `AddSynchronizationContext`/`AddWpfDispatcher`/`AddExecutor`), only the action (with the already-evaluated values) is marshalled to it, and `CreateAdvancedReaction` keeps running its whole body on the executor (#13)
- `ReactionBuilder`'s public constructor takes `IExecutor?` instead of
  `SynchronizationContext?`. `BuildReaction()` / `AddSynchronizationContext` callers are
  unaffected (the context is wrapped in a `SynchronizationContextExecutor`).

### Fixed
- A latent permanent-staleness hole in the lock-based engine — surfaced by the actor-engine work
  (ADR 0006) — where a memo that wired its observer link to an already-dirty source only at the
  end of an un-primed evaluation could commit Clean over that source and never be re-dirtied. It
  is closed by eager capture-time subscription (the observer link is in place before the source
  recomputes, so an in-flight Set bumps the generation and the commit is refused) and guarded by
  `RegressionTests.LockEngine_UnprimedChainUnderStorm_NeverStrandsStale`. The actor engine reaches
  the same guarantee via read-evidence pairs.
- Debounced reaction updates no longer capture the scheduling thread's SynchronizationContext for their continuation, so creating a reaction (or setting a signal) on a UI thread no longer evaluates the dependency graph on that thread (#13)

## [0.1.0] - 2023-10-06
 
### Added
- Added Structured Concurrency primitives
   
### Changed
- Made MemoizR async
 
### Fixed

## [0.0.4] - 2023-09-18
 
### Added
- Added Reactive MemoizR
   
### Changed
- 
 
### Fixed

## [0.0.3] - 2023-09-15
 
### Added
- Context override
   
### Changed
- 
 
### Fixed

## [0.0.2] - 2023-08-30
 
### Added
- possibility to override default equality
   
### Changed
 
### Fixed
- possible concurrency problem when evaluating the graph

## [0.0.1] - 2023-08-29
 
### Added
- Initial Implementation

### Changed
 
### Fixed
