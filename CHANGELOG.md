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
