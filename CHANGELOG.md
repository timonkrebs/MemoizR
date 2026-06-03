# Change Log
All notable changes to this project will be documented in this file.
 
The format is based on [Keep a Changelog](http://keepachangelog.com/)
and this project adheres to [Semantic Versioning](http://semver.org/).

## [Unreleased]

### Fixed
- **C2 — faulting sources are no longer swallowed.** A node could previously be left `CacheClean`
  while holding a pre-error (stale) value when an upstream source faulted (the fault was suppressed
  via `ConfigureAwaitOptions.SuppressThrowing` and never inspected). The fault now propagates out of
  `Get()`, and a faulted `MemoizR` node stays dirty so it re-evaluates on the next access instead of
  serving a stale value.
- **C1 — per-flow scope identity + unbounded scope leak.** The `Context.ReactionScope` getter no
  longer mints a brand-new scope (and `ContextLock`) on every access for an async flow that never
  established one, and `Set` now establishes the flow scope (mirroring `Get`). The scope store was
  reimplemented on `AsyncLocal<ReactionScope>`, eliminating the dictionary that grew without bound.
- **Flaky CI test suite.** The reactive tests asserted exact state after fixed `await Task.Delay(n)`
  waits, but the reaction pipeline updates observers fire-and-forget (debounced), so on constrained
  CI agents the work hadn't run yet and tests intermittently failed or hit tight per-test timeouts
  (the suite failed ~87% of local repeat runs). Fixes, all in the test project only — no library
  behavior change: (1) `xunit.runner.json` disables test-collection parallelism so the
  timing-sensitive tests stop starving each other's thread pool on 2-core runners; (2) a new
  `Eventually.Until` helper polls for the expected state instead of sleeping a fixed interval;
  (3) a module initializer pre-grows the thread pool to remove cold-start starvation;
  (4) over-tight `[Fact(Timeout=…)]` deadlock guards were widened (short-circuit/cancellation
  timeouts that encode behavior were left intact); (5) the CI job `timeout-minutes` was raised from
  2 to 15 (the old cap killed healthy jobs mid-build) and the matrix now uses `fail-fast: false`.

### Changed
- **Behavioral change:** `Get()` now throws when an upstream source faults instead of returning a
  stale value. Callers that previously received a stale-but-non-throwing value will now observe the
  exception.

### Notes
- The per-flow `ContextLock` serializes graph evaluation *within* an async flow, not across
  independent threads/flows: two unrelated flows each get their own scope and lock. A global
  "only one thread evaluates the graph at a time" guarantee is therefore not provided by this lock
  alone — see the threading-model open question in `CODE_REVIEW.md`.

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
