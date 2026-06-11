# Architecture Decision Records

This directory holds Architecture Decision Records (ADRs) for MemoizR — short documents that
capture a significant decision, its context, and its consequences.

| # | Title | Status |
|---|-------|--------|
| [0001](0001-volatile-field-usage.md) | Use of `volatile` fields in MemoizR | Accepted |
| [0002](0002-choosing-a-lock.md) | Choosing a lock: `System.Threading.Lock` vs `AsyncAsymmetricLock` | Accepted |
| [0003](0003-sendable-checking-and-isolation-assertions.md) | Data-race safety at the user boundary: Sendable checking and dynamic isolation assertions | Accepted |
| [0004](0004-compile-time-data-race-diagnostics.md) | Compile-time data-race diagnostics: the MemoizR.Analyzers rule set | Accepted |
| [0005](0005-custom-executors.md) | Custom executors for reactive side effects | Accepted |
| [0006](0006-actor-engine-prototype.md) | The actor-engine prototype: GraphActor turns instead of locks | Accepted (experimental) |

New ADRs are numbered sequentially (`NNNN-title.md`).

See also: [Concurrency Architecture](../architecture/concurrency.md) — the mechanism-level deep
dive (synchronization layers, the cache-state protocol, the lock-free read path, the generation
guard, structured concurrency) with diagrams; the ADRs record the decisions it builds on.
