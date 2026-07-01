# ADR 0005 — Custom executors for reactive side effects

- Status: Accepted
- Date: 2026-06-10
- Deciders: MemoizR maintainers
- Issue: [#36 — Strengthen data-race safety guarantees](https://github.com/timonkrebs/MemoizR/issues/36)
- Builds on: [ADR 0003](0003-sendable-checking-and-isolation-assertions.md) (dynamic isolation assertions)

## Context

The last Swift mechanism issue #36 points at that had no MemoizR counterpart is **custom actor
executors** ([SE-0392]): controlling *where* isolated code runs, plus assertion APIs tied to that
seat of execution. MemoizR had an embryo of this — `AddSynchronizationContext` posted a
reaction's `Execute` to a captured `SynchronizationContext` — but it was hardwired to one
mechanism (UI contexts), unavailable per reaction, invisible to the isolation-assertion layer,
and not a seat anything else (e.g. a future graph actor, the issue's layer 5) could reuse.

## Decision

### The abstraction: `IExecutor` (core)

```csharp
public interface IExecutor
{
    void Enqueue(Action work);   // Swift's enqueue(job): decide WHERE, fire-and-forget
    bool IsCurrent { get; }      // "am I on this executor?" — for isolation assertions
}
```

The shape is deliberately Swift's `enqueue(job)`, not `Task Run(Func<Task>)`: completion
tracking, exception marshalling, exactly-once TCS completion, and the
"continuations must not run inline in the executor's slot" rule all stay in
`ReactionBase.InvokeExecute` — implemented once, correctly, in the library. An implementor
decides only where the delegate runs; the delegate handed to `Enqueue` never throws. (The
alternative shape pushes exactly the subtleties the old `InvokeExecute` comments warn about onto
every implementor — see Alternatives.)

`IsCurrent` is the executor-flavored isolation primitive, with the same point-in-time semantics
as the ADR 0003 assertions; `executor.AssertIsolated()` is the `preconditionIsolated()` analog
for executor-isolated state (UI elements, single-threaded caches).

### Built-in executors

- **`SynchronizationContextExecutor`** — adapts a `SynchronizationContext`;
  `AddSynchronizationContext` now wraps the supplied context in one, so the reaction pipeline
  knows a single concept. `IsCurrent` compares `SynchronizationContext.Current` by reference:
  exact for contexts that install themselves while running callbacks (UI contexts do),
  best-effort *false* otherwise — the safe direction for an assertion.
- **`DedicatedThreadExecutor`** — one background thread, FIFO queue, and an installed
  `SynchronizationContext` so async continuations of enqueued work *return to the thread*:
  state touched only from this executor is single-threaded by construction, across awaits — the
  closest .NET gets to an actor's serial executor. Lifecycle decisions, each pinned by a test:
  - **Dispose drains**: remaining items run; `Dispose` joins the thread (skipped when called
    from the executor itself, where joining would deadlock); the *loop* owns the queue's
    disposal so a racing `Enqueue` never pulls the collection out from under the consumer.
  - **Nothing is lost after shutdown**: an enqueue or posted continuation that arrives after
    `CompleteAdding` falls back to the thread pool — a dropped continuation would leave its
    awaiter pending forever. Isolation is documented to end at dispose (`IsCurrent` is false in
    fallback work).
  - **A throwing posted callback cannot wedge the queue**: the loop catches, keeps consuming,
    and re-throws the exception on the thread pool — preserving the platform's "async-void
    exceptions are fatal" convention while every queued item behind the bad one still runs.

### Wiring

- `MemoFactory` holds `internal IExecutor? Executor` (replacing the `SynchronizationContext`
  property; same lifetime rationale as before).
- `AddExecutor(IExecutor)` on the factory (next to `AddSynchronizationContext`, which is now a
  one-line wrapper and keeps existing callers source-compatible).
- `ReactionBuilder.AddExecutor(IExecutor)` overrides the factory default per builder — the
  analog of a Swift actor declaring its own `unownedExecutor`.
- **Breaking change** (pre-1.0, accepted): `ReactionBuilder`'s public constructor now takes
  `IExecutor?` instead of `SynchronizationContext?`. The mainstream path
  (`BuildReaction()` / `AddSynchronizationContext`) is unaffected; the generator script
  (`GenerateReactionFactories.ps1`) was updated in lockstep.

## Consequences

Positive:

- Side effects can be pinned to any seat of execution — UI context, dedicated thread, test
  executor — through one concept, per factory or per reaction, and code can *assert* it runs
  there. All four SE-0392 ingredients (executor protocol, default seat, per-declaration
  executor, isolation assertions) now have counterparts.
- `DedicatedThreadExecutor` gives users a true serial isolation domain today, without waiting
  for the layer-5 graph actor — and that actor, if built, runs on this same abstraction.
- The careful TCS/exception semantics of the old SynchronizationContext path are preserved
  verbatim and now guard every executor, not just contexts.

Costs / accepted trade-offs:

- `IsCurrent` for wrapped SynchronizationContexts is best-effort (a context that does not set
  `Current` reports not-isolated). Exact identity needs an executor type the library controls,
  which `DedicatedThreadExecutor` is.
- One public constructor break (`ReactionBuilder`), recorded in the CHANGELOG.

## Alternatives considered

- **Use `TaskScheduler` as the abstraction.** Standard, but wrong-shaped: scheduling async work
  needs `Unwrap` plumbing, there is no isolation introspection to build `AssertIsolated` on, and
  `TaskScheduler.FromCurrentSynchronizationContext()` must be called *on* the context — the one
  place the configuring code usually is not. A custom `TaskScheduler` can still be adapted to
  `IExecutor` in a few lines.
- **`Task Run(Func<Task> work)` interface shape.** Rejected: every implementor would have to get
  `RunContinuationsAsynchronously`, exactly-once completion, and exception marshalling right —
  the precise trap the existing `InvokeExecute` comments document. `Enqueue(Action)` makes the
  implementor's contract "run this somewhere, eventually" and nothing else.
- **Automatically asserting `IsCurrent` inside the enqueued callback** (dynamic SE-0423 check at
  the boundary). Rejected: legitimate `SynchronizationContext` implementations run callbacks
  without setting `Current`, so the assert would reject working configurations. User code can
  opt in by calling `executor.AssertIsolated()` at the top of its `Execute`.
- **Executor for the graph's bookkeeping (not just reactions).** That is issue #36's layer 5
  (the GraphActor of ADR 0003's "Known limitations"); this ADR deliberately ships the seat it
  would run on without rewriting the synchronization core.

[SE-0392]: https://github.com/apple/swift-evolution/blob/main/proposals/0392-custom-actor-executors.md
