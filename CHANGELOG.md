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
  in both the analyzer and `SendableChecker`.
- Custom executors for reactive side effects (issue #36, the Swift SE-0392 analog, see ADR
  0005): `IExecutor` (`Enqueue`/`IsCurrent`) with `executor.AssertIsolated()`,
  `SynchronizationContextExecutor`, a `DedicatedThreadExecutor` whose installed
  SynchronizationContext keeps async continuations on its thread (a true serial isolation
  seat), `MemoFactory.AddExecutor(...)`, and a per-builder `ReactionBuilder.AddExecutor(...)`
  override.

- EXPERIMENTAL actor engine (issue #36 layer 5, see ADR 0006): `CreateActorSignal` /
  `CreateActorMemoizR` nodes whose graph bookkeeping runs as synchronous turns of a per-context
  `GraphActor` (a serial channel loop that also implements `IExecutor`) instead of under locks —
  same observable semantics (lazy memoization, generation-guarded commits, diamond absorption,
  dynamic rewiring, lock-free clean fast path), no monitors, no deadlock surface, plus a
  read-evidence guard (`(source, generation)` pairs re-verified at commit) that closes the
  late-wired-observer staleness hole.

### Changed
- `ReactionBuilder`'s public constructor takes `IExecutor?` instead of
  `SynchronizationContext?`. `BuildReaction()` / `AddSynchronizationContext` callers are
  unaffected (the context is wrapped in a `SynchronizationContextExecutor`).

### Known issues
- The lock-based engine can permanently strand a memo Clean-but-stale when its observer link
  wires while the source is already dirty (an UN-primed graph evaluated mid-storm): cascade
  suppression at already-dirty nodes silences late-wired observers forever. Discovered by the
  actor-engine work; reproducer quarantined as
  `RegressionTests.LockEngine_UnprimedChainUnderStorm_StrandsStale_KnownIssue`; the actor
  engine's read-evidence guard is the proven fix shape (ADR 0006).

### Fixed

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
