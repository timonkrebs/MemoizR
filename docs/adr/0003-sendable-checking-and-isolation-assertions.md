# ADR 0003 — Data-race safety at the user boundary: Sendable checking and dynamic isolation assertions

- Status: Accepted
- Date: 2026-06-10
- Updated: 2026-06-10 — the reference-type rule gained a settable-property surface (a non-private
  non-init setter rejects the type), kept in lockstep with the MZR001 analyzer where that rule is
  what catches metadata types whose private fields the compiler does not import; the planned
  compile-time layer shipped as [ADR 0004](0004-compile-time-data-race-diagnostics.md).
- Deciders: MemoizR maintainers
- Issue: [#36 — Strengthen data-race safety guarantees](https://github.com/timonkrebs/MemoizR/issues/36)

## Context

Issue #36 asks for Swift-6-style data-race safety. Swift's guarantee is not one feature but
several cooperating mechanisms: actor **isolation domains** serialize access to mutable state,
**`Sendable` checking** restricts which types may cross domains (enforced transitively by the
compiler), strict **global-variable rules** ([SE-0412]), **dynamic isolation assertions**
([SE-0423]) cover boundaries the compiler cannot see, and **custom executors** ([SE-0392])
control where isolated code runs.

MemoizR already has the isolation-domain half for its *own* state: the per-flow `ContextLock`,
the per-node mutex, the generation guard, and the volatile fast path make the graph's internal
bookkeeping race-free (ADR 0001, ADR 0002, and the [concurrency architecture]). What it did not
have is the `Sendable` half. The unchecked surface is the **user boundary**:

1. **Mutable value types crossing flows.** `MemoHandlR<T>.Value` publishes the *reference*
   tear-free (the volatile `ValueBox`), but if `T` is `List<int>` or a mutable POCO, every
   consumer on every flow shares the same instance, and nothing stops one flow from mutating it
   while another reads it — including readers on the lock-free `Get` fast path.
2. **Computation closures capturing shared mutable state.** Different nodes' functions evaluate
   concurrently on different flows (and `StructuredReduceJob` runs sibling functions in parallel
   on one shared scope), so captured mutable locals/fields race.
3. **Protocol-violating callers of internal mutators.** The documented contracts ("must only be
   called inside a ContextLock-serialized evaluation") were prose, enforced by review only.

C# has no compiler-enforced `Sendable`, so the guarantee has to be assembled from runtime
checking now and Roslyn analyzers later. This ADR records the runtime layer.

## Decision

### 1. A `Sendable` vocabulary: `[Sendable]` + `SendableChecker`

`SendableChecker` (MemoizR core) structurally verifies that a closed type is safe to share
across concurrently running flows — every value of it is deeply immutable or internally
synchronized:

- primitives, enums, and a small green-list of known-immutable/known-synchronized BCL types
  (`string`, `decimal`, date/time types, `Guid`, `Uri`, `Version`, `BigInteger`,
  `CancellationToken`, `Task`, `Task<T>` with a Sendable `T`);
- collections in `System.Collections.Immutable` / `.Frozen` / `.Concurrent`, when their type
  arguments are Sendable (the elements are what consumers share);
- **value types** whose instance fields are all of Sendable type. The fields may be writable:
  every read of a struct value yields a private copy, so only *references* reachable from the
  copy can alias shared state. (This is why tuples pass.)
- **reference types** whose instance fields — across the whole inheritance chain — are all
  readonly (init-only counts) *and* of Sendable type. One instance is shared by all consumers,
  so both the slot and the reachable object graph must be immutable.
- anything marked **`[Sendable]`** — trusted without structural checks, the analog of Swift's
  `@unchecked Sendable`, for internally synchronized types the walk cannot prove. Deliberately
  non-inherited: a derived type can add mutable state, so each type promises for itself.

Interfaces, abstract classes, `object`, delegates, and arrays are rejected: the first three
because the static type proves nothing about the runtime implementation, delegates because they
can capture arbitrary state, arrays because their elements are always writable. Self-referential
types (linked records) terminate via a coinductive cycle assumption — mutability is detected at
the field where it occurs, so re-entering a type proves nothing. Verdicts are cached per closed
type (failure reasons are built as a chain that names the offending member, with auto-property
backing fields reported as the property the user wrote). Only the top-level entry caches: a
verdict computed mid-recursion can rest on an unproven cycle assumption about an outer type.

### 2. Opt-in creation-time enforcement: `MemoFactoryOptions.StrictSendableChecks`

`new MemoFactory(key, MemoFactoryOptions.StrictSendableChecks)` makes every value-bearing
creation validate its value type and throw `InvalidOperationException` with the structural
reason and fix guidance. Enforced on: `CreateSignal<T>`, `CreateEagerRelativeSignal<T>`,
`CreateMemoizR<T>`, `CreateConcurrentMap<T>` (the *element* type — the enumerable wrapper is
rebuilt per recompute, the elements are the shared payload), `CreateConcurrentMapReduce<T>`, and
`CreateConcurrentRace<T, R>` (both `T` and `R`: the resolver's `R` is handed to every racing
child in parallel).

Design points:

- **Creation-time, not write-time.** The check costs one cached dictionary probe per node
  creation and nothing per `Set`/`Get`. Checking each written *instance's runtime type* would
  put reflection on the write hot path; rejected (see Alternatives).
- **Per-factory, not per-context.** Strictness governs how *this* factory creates nodes; a
  strict and a lax factory may deliberately share one keyed context (e.g. migrating a codebase
  incrementally, exactly like Swift's per-module `-strict-concurrency` staging).
- **Off by default.** Existing users see zero behavior change.
- Reactions are not enforcement points: they store no new value type — the values they read were
  validated where their source nodes were created.

### 3. Dynamic isolation assertions (the SE-0423 analog)

The `AsyncAsymmetricLock` already converts impossible same-flow waits into exceptions — that
*is* dynamic isolation enforcement. This layer generalizes it:

- **`AsyncAsymmetricLock.IsHeldByCurrentFlow`** — whether the current async flow holds the lock,
  using the same AsyncLocal scope that drives reentrancy. A child task spawned inside the locked
  region inherits the scope and counts as the holding flow (it would be granted a recursive
  acquisition — the `StructuredReduceJob` model). The answer is a point-in-time snapshot: valid
  for asserting "I am inside the locked region", never for deciding to skip an acquisition.
- **`Context.IsEvaluationIsolated` / `Context.AssertEvaluationIsolated()`** and the
  **`MemoFactory.AssertEvaluationIsolated()`** passthrough — the public
  `preconditionIsolated()` analog: throws when the current flow is not inside a
  MemoizR-serialized graph evaluation. A flow with no pinned scope resolves a throwaway scope
  whose lock was never acquired, so it correctly reads as not isolated.
- **A DEBUG-only assert in `SignalHandlR.UpdateSourceAndObserverLinks`** mechanically pins its
  documented contract ("must only be called inside a ContextLock-serialized evaluation"). Every
  Debug test run now exercises it on every recompute; a future caller that reaches the rewiring
  without the lock fails loudly instead of corrupting links silently. `RemoveParentObservers` is
  deliberately not asserted: `ReactionBase.Dispose` legitimately prunes links outside any
  evaluation. Release builds compile the assert out (`[Conditional("DEBUG")]`), keeping the
  recompute path free of the extra scope resolution.

## Known limitations (and the planned next layers)

- **No compile-time enforcement.** ~~A Roslyn analyzer package is the planned second layer~~ —
  shipped as [ADR 0004](0004-compile-time-data-race-diagnostics.md): MZR001 (non-Sendable type
  arguments), MZR002 (captured-mutable-state writes in computation lambdas, the SE-0412 analog),
  MZR003 (`Set` inside a computation as a build diagnostic instead of a runtime lock exception).
- **Subclass smuggling.** A non-sealed class is judged by its declared structure; a mutable
  subclass behind an upcast is not caught at creation time. Swift closes this by requiring
  Sendable classes to be final; rejecting all non-sealed classes here would reject most records
  and make strict mode unusable. Accepted, documented; the analyzer layer can warn on it.
- **`[Sendable]` is trusted.** Like `@unchecked Sendable`, a false promise reintroduces the race.
- **Closures and statics are out of scope at runtime.** Captured state never flows through a
  factory API where a runtime check could see it; this is inherently analyzer territory.
- **Actor-izing the graph core** (the issue's literal suggestion, with SE-0392-style pluggable
  executors) is deferred: it buys by-construction safety for *internal* state that ADR 0001/0002
  already cover, at the cost of rewriting the most battle-tested code in the repo. This layer
  delivers the user-facing guarantees first.

## Consequences

Positive:

- The library finally *sees* the most common user-induced data race (mutable shared values) and
  can refuse it at the boundary, with an explanation that names the offending member.
- The Swift mapping gives the work a principled shape: `[Sendable]`/checker ↔ `Sendable`,
  strict factory ↔ `-strict-concurrency`, assertion APIs ↔ SE-0423, lock introspection ↔
  `Actor.preconditionIsolated`.
- Documented internal contracts became executable: the whole Debug test suite now proves the
  rewiring contract on every recompute.

Costs / accepted trade-offs:

- One cached type-check per node creation in strict mode; zero cost on reads/writes and zero
  when the option is off.
- `SendableChecker`'s verdicts are conservative: internally synchronized types it cannot prove
  must opt in via `[Sendable]`.

## Alternatives considered

- **An `ISendable` marker constraint (`where T : ISendable`) on the factory APIs.** Compile-time
  and transitive-ish, but viral and breaking: every primitive, BCL type, and third-party type
  would need wrappers. Rejected; the attribute + runtime check keeps the API unconstrained and
  the analyzer layer will recover the compile-time signal.
- **Validating every written instance's runtime type (per `Set`).** Catches subclass smuggling,
  but puts a check on the write hot path and still misses interior mutation. Rejected for the
  default; could become an additional opt-in diagnostic mode if needed.
- **Deep-freezing / defensive copying of values.** Changes value identity semantics
  (`Equals`-based cutoffs, reference comparisons) and costs per write. Rejected.
- **`Debug.Assert` for the isolation checks.** A failed `Debug.Assert` fail-fasts the process in
  .NET instead of faulting the offending operation; throwing `InvalidOperationException` matches
  the lock's existing deadlock-to-exception conversions and is testable. Chosen accordingly.

[SE-0412]: https://github.com/apple/swift-evolution/blob/main/proposals/0412-strict-concurrency-for-global-variables.md
[SE-0423]: https://github.com/apple/swift-evolution/blob/main/proposals/0423-dynamic-actor-isolation.md
[SE-0392]: https://github.com/apple/swift-evolution/blob/main/proposals/0392-custom-actor-executors.md
[concurrency architecture]: ../architecture/concurrency.md
