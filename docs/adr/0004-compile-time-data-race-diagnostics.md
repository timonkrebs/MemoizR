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
   record. The green-lists (known collections, known immutables, `Task<T>`) additionally
   require the symbol to come from a framework assembly and not from source: a source-declared
   lookalike (`namespace System { class Uri { public int State; } }`) binds over the BCL type
   and must go through the structural walk, exactly as the runtime's `typeof` identity match
   treats it; the same identity rule guards the `[Sendable]` attribute, and the factory-method
   classification itself (a source-shadowed `MemoizR.MemoFactory` must not draw MZR001–003 onto
   unrelated APIs). A metadata type with *purely private* mutable state still passes the analyzer
   silently — that includes a `{ get; private set; }` auto-property on a referenced assembly,
   whose private setter and writable backing field are both invisible under public-only import,
   making it indistinguishable from a get-only property; the runtime strict mode remains the
   backstop there. (Value types stay exempt from the field and settable-property rules: every
   read hands out a copy.)

### MZR002 — reactive computation mutates state shared with code outside it

The SE-0412 analog, scoped to stay high-signal. Inside any computation passed to
`CreateMemoizR`, the structured-concurrency creations, or `ReactionBuilder.CreateReaction` /
`CreateAdvancedReaction` — a lambda, a method group / local function whose declaration lives in
the same file, or a delegate variable whose same-tree initializer holds the computation (later
reassignments are dataflow the analyzer does not chase; other trees have no operation model in
the analysis — the runtime checks cover both) — a **write** to:

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
is analyzed exactly once — while the nested creation's ORDINARY arguments (a label expression,
say) still belong to the outer walk: they are evaluated during the outer evaluation.

### MZR003 — `Set` inside a reactive computation

`Signal.Set`/`EagerRelativeSignal.Set` inside a computation whose **own flow already holds the
evaluation lock in upgradeable mode** is an exclusive-inside-upgradeable acquisition, which
`AsyncAsymmetricLock` deliberately converts into an `InvalidOperationException` (ADR 0002; a
write inside a read of the same graph is a feedback loop, and waiting would deadlock). The rule
surfaces that runtime exception at build time.

Host scoping follows the lock semantics exactly: `CreateMemoizR`, `CreateConcurrentMapReduce`
(its children share the parent flow's scope), and the reaction builders are flagged;
`CreateConcurrentMap` and `CreateConcurrentRace` are **not**, because their children run on
forced fresh scopes where the same-flow conflict does not exist. A Set whose target signal
*provably* belongs to a **different context** than the host is not flagged either — the write
locks the target's own context, where the computing graph holds nothing, so the runtime does
not throw. Both factories must resolve (through receiver chains and same-tree initializers) to
different symbols AND to provably disjoint contexts — unkeyed `new MemoFactory()` instances
each own a fresh context, keyed ones share per constant key, so two factories with the same key
keep the diagnostic. Anything unprovable (a field wired in a constructor, a parameter, a
non-constant key) keeps the diagnostic, since one shared context is the overwhelmingly common
case and the runtime exception is deterministic there.

The walk is likewise scoped to the lock semantics: only the computation's **direct execution
path** is inspected. Nested anonymous functions and local-function declarations are pruned,
because a callback the computation merely *builds* — the diagnostic's own fix guidance,
"schedule the write outside the evaluation" — runs later on a flow that holds no evaluation
lock. (The cost is a false negative for a nested function invoked synchronously inside the
computation; the runtime exception still guards that path. MZR002 keeps the full walk: a
captured-state write is a data race whenever the callback runs, deferred or not.)

### MZR004 — optimistic patch captures non-Sendable state

The closure-capture mirror of MZR001, closing the strict-mode gap recorded in ADR 0007: a patch
passed to `OptimisticActionContext.Apply` is stored in the overlay and **re-executed by the
view's computation on whichever flow pulls** the optimistic state, so everything the patch's
closure captures crosses flows exactly like a node value — but the *runtime* cannot check it
(closure display classes always carry writable fields, so a structural runtime check would
reject every capturing lambda, immutable captures included).

Flagged, once per captured symbol per patch:

- a **captured local or parameter whose type is not Sendable** (the same classifier verdicts as
  MZR001; type parameters keep the benefit of the doubt);
- a read of **writable state on the enclosing object** (a non-readonly field, a settable
  property; `init` counts as immutable, and a ref-returning property counts as writable — no
  setter, but it hands out assignable live storage) — the patch re-reads it on other flows
  while the owner mutates it freely;
- a read of a **readonly/get-only member whose type is not Sendable** — the object handed out
  is what gets shared. Computed get-only property bodies are not chased. Both member verdicts
  refine a *non-Sendable* enclosing object only: an enclosing type the classifier accepts
  (`[Sendable]`, structurally immutable) is trusted wholesale, exactly as the runtime checker
  and MZR001 trust it;
- a **method-group patch's receiver** (`ctx.Apply(state, helper.Patch)`, directly or stored in
  a delegate variable first) — the receiver is captured into the stored delegate even when the
  method body lives in metadata and cannot be walked. A *mutable struct* receiver is flagged
  when the referenced method is non-readonly: the Sendable verdict for a value type rests on
  copy semantics, but the delegate stores one boxed copy that a non-readonly method mutates in
  place (extension methods never reach this verdict — CS1113 forbids creating a delegate from
  a value-type extension receiver);
- a **bare `this`** handed to a helper (`x => ReadCounter()`, `Use(this)`) — the whole
  enclosing object is captured with no member to inspect, so it is held to its type's
  sendability: hiding the read behind a helper must not evade the rule;
- a read of **static state** that is writable or of a non-Sendable type (`const` is a
  compile-time value) — statics are shared across every flow without any capture at all, so
  same-tree helper methods the patch calls are chased for them transitively (the classifier
  deliberately ignores statics, meaning a Sendable `this` says nothing about them);
- the **closure of a local function** the patch calls that is declared *outside* the patch —
  no receiver/`this` verdict covers it, so its body is inspected for captures against its own
  declaration scope. Only captures declared in a function *enclosing the patch* count: one
  declared inside the patch is patch-internal, and a local of a *called helper* (captured by a
  local function nested in that helper) is recreated on every execution, not stored in the
  delegate;
- an **already-built delegate that resolves to nothing walkable** (a `Func<T,T>`
  field/parameter with no same-tree initializer, or a variable *reassigned* after its
  initializer — the overlay may store the second closure) — the overlay stores it all the
  same, this rule is the only check a patch ever gets, and a delegate can capture arbitrary
  mutable state: unverifiable means flagged, like the classifier's unverifiable categories.

Reads of Sendable-typed captures stay unflagged: capturing the action payload or other
immutable snapshots is the idiomatic pattern. Captured-state **writes** inside a patch are
MZR002's territory — `Apply` is classified as a computation host (the patch is genuinely
engine-executed, unlike action *bodies*, which are user-driven process code and deliberately
not hosts) — and a `Set` inside a patch is MZR003's, since the patch runs inside the view
memo's recompute whose flow holds the evaluation lock.

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
