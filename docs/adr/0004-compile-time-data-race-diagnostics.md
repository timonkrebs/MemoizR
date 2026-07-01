# ADR 0004 — Compile-time data-race diagnostics: the MemoizR.Analyzers rule set

- Status: Accepted
- Date: 2026-06-10
- Deciders: MemoizR maintainers
- Issue: [#36 — Strengthen data-race safety guarantees](https://github.com/timonkrebs/MemoizR/issues/36)
- Builds on: [ADR 0003](0003-sendable-checking-and-isolation-assertions.md) (the runtime layer)

## Context

ADR 0003 added the runtime half of Swift-style data-race safety: `SendableChecker`, strict
factory mode, and dynamic isolation assertions. Its first listed limitation was the absence of
compile-time enforcement — the thing that makes Swift 6's guarantee a *guarantee* rather than a
runtime surprise. C# has no `Sendable` in the language, so the compile-time half has to be a
Roslyn analyzer package. This ADR records that package's design: which rules ship, what each
deliberately does and does not flag, and the constraints Roslyn imposes.

## Decision

A new `MemoizR.Analyzers` project (netstandard2.0, Roslyn 4.8 floor so any SDK ≥ 8 can load it)
ships **inside the MemoizR NuGet package** (`analyzers/dotnet/cs`), so every consumer gets the
rules on build with no extra reference. All rules default to **Warning** — the Swift 5.x
"strict concurrency warnings" migration posture — and are configurable per project via
`.editorconfig` (`dotnet_diagnostic.MZR001.severity = error|suggestion|none`).

### MZR001 — non-Sendable value type at a creation site

The build-time mirror of `MemoFactoryOptions.StrictSendableChecks`: every generic type argument
of a value-bearing factory creation (`CreateSignal`, `CreateEagerRelativeSignal`,
`CreateMemoizR`, `CreateConcurrentMap`, `CreateConcurrentMapReduce`, `CreateConcurrentRace`) is
classified by a symbol-based port of `SendableChecker`. Checking the method's `TypeArguments`
uniformly covers `ConcurrentRace`'s resolver result `R` — handed to every racing child in
parallel — for free.

**The lockstep contract.** `SendableSymbolClassifier` (symbols) and `SendableChecker`
(reflection) implement the same classification and must be edited together; a type one accepts
and the other throws on erodes trust in both. Two deliberate divergences exist, both forced by
what a compiler can and cannot see:

1. **Unbound type parameters pass.** There is no `Sendable` constraint to require on a generic
   passthrough (`Signal<T> Make<T>(...)`), so flagging it would force suppressions rather than
   fixes. The closed instantiation is checked at its own creation site; the runtime check covers
   whatever the analyzer could not see.
2. **Private metadata fields are invisible.** Compilations default to
   `MetadataImportOptions.Public`, so the analyzer cannot see `List<int>`'s private `_items` —
   and it cannot opt out: import options belong to the user's compilation, not the analyzer.
   This forced two rules *both* checkers now share: **a non-private settable (non-init) property
   is mutability evidence** on a reference type, and **a visible get-only property's TYPE must
   itself be Sendable**. `List<T>` is caught at compile time by its settable `Capacity`/indexer
   instead of its invisible fields; a get-only `List<int> Items { get; }` on a metadata class is
   caught through the property's type instead of the invisible backing field (and, at runtime, a
   *computed* get-only property handing out shared static state — the one shape the field walk
   can never see). The rules are principled rather than a BCL hand-list — a visible mutation
   surface makes a shared instance mutable regardless of the backing storage — and mirroring them
   in the runtime checker keeps the verdicts aligned. The property-type rule required one
   green-list addition on both sides: `System.Type` (runtime-managed, effectively immutable),
   because every non-sealed record synthesizes `protected virtual Type EqualityContract { get; }`
   and `Type` is abstract — without the green-list the rule would falsely reject every non-sealed
   record. A metadata type with *purely private* mutable state still passes the analyzer
   silently — that includes a `{ get; private set; }` auto-property on a referenced assembly,
   whose private setter and writable backing field are both invisible under public-only import,
   making it indistinguishable from a get-only property; the runtime strict mode remains the
   backstop there. (Value types stay exempt from the field and settable-property rules: every
   read hands out a copy.)

### MZR002 — reactive computation mutates state shared with code outside it

The SE-0412 analog, scoped to stay high-signal. Inside any computation passed to
`CreateMemoizR`, the structured-concurrency creations, or `ReactionBuilder.CreateReaction` /
`CreateAdvancedReaction` — a lambda, or a method group / local function whose declaration lives
in the same file (other trees have no operation model in the analysis; the runtime checks cover
them) — a **write** to:

- a local or parameter captured from the enclosing method,
- a field of the enclosing object (through `this`), or
- a static field

is flagged, with the fix suggestion being the library's own model: lift the state into a
`Signal`/`EagerRelativeSignal`. Writes are simple/compound/coalesce assignments, `++`/`--`,
deconstructions (flattened through nested tuples: `(a, (b, c)) = ...` writes every leaf),
`ref`/`out` arguments, and non-`readonly` instance-method calls on value-type receivers that
resolve to shared storage (`counter.Increment()` mutates the captured local exactly like
`counter.Value++`; `readonly` members — which includes most BCL structs — and the
object-virtual overrides stay exempt).

Deliberately **not** flagged:

- **Reads of captured state.** Read-only captured configuration is idiomatic, and proving a read
  races requires whole-program knowledge an analyzer does not have. (The existing test suite is
  full of legitimate `WaitForConvergence`-style captured reads.)
- **Mutation through a captured reference** (`capturedList.Add(1)`). That is MZR001's territory:
  the *type* crossing the boundary should be Sendable.
- **Property writes on other objects** — same reasoning.

"Captured" is decided by declaration position (declared outside the computation's declaring
syntax — the lambda expression, or the method/local-function declaration), which keeps nested
non-computation lambdas correct: a LINQ lambda's own local belongs to the computation; the
enclosing method's local does not. A creation nested inside another computation is pruned from
the outer walk — the operation action fires for the nested invocation too, so the inner lambda
is analyzed exactly once.

### MZR003 — `Set` inside a reactive computation

`Signal.Set`/`EagerRelativeSignal.Set` inside a computation whose **own flow already holds the
evaluation lock in upgradeable mode** is an exclusive-inside-upgradeable acquisition, which
`AsyncAsymmetricLock` deliberately converts into an `InvalidOperationException` (ADR 0002; a
write inside a read of the same graph is a feedback loop, and waiting would deadlock). The rule
surfaces that runtime exception at build time.

Host scoping follows the lock semantics exactly: `CreateMemoizR`, `CreateConcurrentMapReduce`
(its children share the parent flow's scope), and the reaction builders are flagged;
`CreateConcurrentMap` and `CreateConcurrentRace` are **not**, because their children run on
forced fresh scopes where the same-flow conflict does not exist.

The walk is likewise scoped to the lock semantics: only the computation's **direct execution
path** is inspected. Nested anonymous functions and local-function declarations are pruned,
because a callback the computation merely *builds* — the diagnostic's own fix guidance,
"schedule the write outside the evaluation" — runs later on a flow that holds no evaluation
lock. (The cost is a false negative for a nested function invoked synchronously inside the
computation; the runtime exception still guards that path. MZR002 keeps the full walk: a
captured-state write is a data race whenever the callback runs, deferred or not.)

### Testing strategy

The analyzer tests compile snippets in-memory **against the real MemoizR assemblies**
(project-referenced; resolved via `TRUSTED_PLATFORM_ASSEMBLIES`) under default compilation
options, and assert the snippet compiles before asserting diagnostics. This is what exposed the
`MetadataImportOptions` constraint — the standard `Microsoft.CodeAnalysis.Testing` framework with
hand-written stubs would have hidden it, which is why it was not used. Notably, the tests
validate real-world conditions (default metadata import) rather than idealized ones.

### What the repo itself does

The repo's own projects do **not** run the analyzers: the library never calls its own factory
methods, and the test suite *deliberately* violates the rules (signals of `List<int>` for strict
mode tests, captured-sum reactions for convergence tests) — wiring the analyzers in would mean
blanket suppressions, which teach readers to ignore the diagnostics. The analyzer test project is
the enforcement that the rules work.

## Consequences

Positive:

- The discipline strict mode enforces at runtime is now visible on every consumer build, at the
  exact creation site, with the member that breaks it named in the message — without running the
  program. Issue #36's "strengthen guarantees" now has a static component.
- The settable-property rule made *both* checkers stricter and kept them aligned.

Costs / accepted limitations:

- Analyzer coverage is best-effort by nature: subclass smuggling, mutation through captured
  references, reads of racy state, and metadata types with purely private mutable state all pass
  the build. Each is either covered by the runtime layer or documented in the rule above.
- Two checker implementations must be maintained in lockstep (the price of "no runtime dependency
  from the analyzer", which Roslyn requires anyway — an analyzer cannot reference the library it
  analyzes).
- Bundling means consumers who want no diagnostics must configure severities rather than skip a
  package. Chosen anyway: an opt-in analyzer package would mostly reach the users who least need
  it.

## Alternatives considered

- **`Microsoft.CodeAnalysis.Testing` + stub APIs** for the tests. Rejected: stubs drift from the
  real factory surface, the framework resolves reference assemblies via NuGet at test runtime,
  and idealized compilations would have masked the metadata-import constraint that shaped MZR001.
- **A separate `MemoizR.Analyzers` NuGet package.** Rejected for now: discoverability is the
  point of this layer; the dll is ~40 KB inside the existing package. Can be split later without
  breaking anyone (analyzer assets are additive).
- **Hand-listing mutable BCL types** (`List`, `Dictionary`, `StringBuilder`, …) to patch the
  metadata gap. Rejected: unbounded and forever incomplete; the settable-property rule derives
  the same verdicts from the type's own shape and applies to third-party packages too.
- **Flagging reads of captured mutable state** (full SE-0412 strictness). Rejected for v1: the
  false-positive rate on idiomatic code would push users to disable MZR002 wholesale, which is
  worse than the narrower write-only rule that survives contact with real codebases.
- **Error severity by default.** Rejected: this layer is the Swift-5.x-style migration step;
  projects opt into `error` per rule via `.editorconfig` when ready (the Swift 6 posture).
