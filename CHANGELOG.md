# Change Log
All notable changes to this project will be documented in this file.
 
The format is based on [Keep a Changelog](http://keepachangelog.com/)
and this project adheres to [Semantic Versioning](http://semver.org/).
## [Unreleased]
 
### Added
- `CreateReaction(...)` convenience overloads directly on the factory — sugar for `BuildReaction().CreateReaction(...)` with the default label and debounce
- MemoizR.Wpf package: `AddWpfDispatcher` routes reaction actions to the WPF UI thread (via `Application.Current.Dispatcher` or an explicit `Dispatcher`) while the dependency graph keeps evaluating on the thread pool (#13)
- Causality Trigger Clock, phase 1 (#39, internal): stable per-context node ids, per-signal
  trigger counters bumped on value-changing Sets, and per-node causality stamps (own stamp +
  one stamp per source) captured at source-read time and published atomically with the value.
  See docs/architecture/causality-trigger-clock.md.
- Causality Trigger Clock, phase 2 (#39, internal): the ITC-inspired space-efficient encoding —
  CausalityStamp is now a canonical, persistent event tree over the id space (uniform regions
  collapse; joins share subtrees; equal maps have identical representations) — plus a compact,
  deterministic binary wire format (Serialize/Deserialize) with defensive validation.
- Causality Trigger Clock, phase 3 (#39): reset resilience via per-context incarnation epochs
  (stamps from a restarted graph are never equal/consistent with, and refuse to join, their
  pre-reset incarnation), the wire format frozen at version 2 (epoch in the header), and the
  public read surface for a distributed sync layer: IStampedGetR<T>.GetWithStamp() on all
  value nodes, public Stamp/SourceStamps/Id on every node, and the public CausalityStamp type
  (creation stays internal).
   
### Changed
- Reactions now evaluate their separate-parameter dependencies in parallel on the thread pool; with a SynchronizationContext registered, only the action (with the already-evaluated values) is marshalled to the context, and `CreateAdvancedReaction` keeps running its whole body on the context (#13)
 
### Fixed
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
