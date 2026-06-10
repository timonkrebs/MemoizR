# Architecture Decision Records

This directory holds Architecture Decision Records (ADRs) for MemoizR — short documents that
capture a significant decision, its context, and its consequences.

| # | Title | Status |
|---|-------|--------|
| [0001](0001-volatile-field-usage.md) | Use of `volatile` fields in MemoizR | Accepted |
| [0002](0002-choosing-a-lock.md) | Choosing a lock: `System.Threading.Lock` vs `AsyncAsymmetricLock` | Accepted |

New ADRs are numbered sequentially (`NNNN-title.md`).

See also: [Concurrency Architecture](../architecture/concurrency.md) — the mechanism-level deep
dive (synchronization layers, the cache-state protocol, the lock-free read path, the generation
guard, structured concurrency) with diagrams; the ADRs record the decisions it builds on.
