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

### Changed

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
