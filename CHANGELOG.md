# Change Log
All notable changes to this project will be documented in this file.
 
The format is based on [Keep a Changelog](http://keepachangelog.com/)
and this project adheres to [Semantic Versioning](http://semver.org/).
## [Unreleased]
 
### Added
- MemoizR.Wpf package: `AddWpfDispatcher` routes reaction actions to the WPF UI thread (via `Application.Current.Dispatcher` or an explicit `Dispatcher`) while the dependency graph keeps evaluating on the thread pool (#13)
   
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
