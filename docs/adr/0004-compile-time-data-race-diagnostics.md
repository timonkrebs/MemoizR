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
rules on build with no extra reference. Since the Swift-6-parity step (issue #145 part A4),
MZR001–003 default to **Error** — the Swift 6 language-mode posture, matching the runtime
checks being on by default — while the newer heuristic rules stay softer (MZR004/005 Warning,
MZR006 Info). Everything is configurable per project via `.editorconfig`
(`dotnet_diagnostic.MZR001.severity = warning|suggestion|none` is the migration posture).

### MZR001 — non-Sendable value type at a creation site

The build-time mirror of `MemoFactoryOptions.StrictSendableChecks`: every generic type argument
of a value-bearing factory creation (`CreateSignal`, `CreateEagerRelativeSignal`,
`CreateMemoizR`, `CreateConcurrentMap`, `CreateConcurrentMapReduce`, `CreateConcurrentRace`) is
classified by a symbol-based port of `SendableChecker`. Checking the method's `TypeArguments`
uniformly covers `ConcurrentRace`'s resolver result `R` — handed to every racing child in
parallel — for free.

**The escape hatch is honored at build time too.** The runtime accepts a per-factory opt-out
(`MemoFactoryOptions.DisableSendableChecks`); with an Error default, a creation on such a
factory must not fail the build on the very checks its runtime disabled, or migration would
need a project-wide suppression on top of the documented option. Receiver resolution is
best-effort and conservative in the safe direction (`FactoryOptOut`), demanding *definite*
evidence on three axes: the factory's construction must be in sight — an inline
`new MemoFactory(options: …DisableSendableChecks)` receiver (followed through the library's
fluent configuration chain — the named whitelist `AddExecutor`/`AddSynchronizationContext`/
`AddTimeProvider`/`AddWpfDispatcher` mutates and returns the same factory, applied to direct
receivers and initializer values alike; generic passthroughs like `Untrack<T>` return their
delegate's result and are not followed), or a local / readonly field / get-only property whose
initializer in the *same file* is one (analyzers may not call `Compilation.GetSemanticModel`, which keeps cross-file
initializers out of reach; settable slots could be repointed from anywhere, and a member of a
partial type split across files is not trusted either, since another file's constructor can
overwrite the visible initializer) — the options argument must *fold to a constant*
carrying the flag (a conditional `useLax ? DisableSendableChecks : None` still runs strict on
one path), and any write to the receiver symbol elsewhere in the file revokes the
initializer's authority (the local may have been repointed at a strict factory before the
creation). Anything short of that — a factory parameter, options computed at runtime — keeps
the checks on: a missed opt-out costs one suppression, a wrong opt-out would silently drop
the rule. MZR006 honors the same opt-out (smuggling is a hole in checks that factory
disabled); MZR002/003 do not, because they diagnose races and deterministic runtime throws
that exist regardless of Sendable checking.

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

### MZR004 — static state next to the graph (the SE-0412 analog proper)

Swift 6 rejects every non-isolated mutable global, because a global is reachable from every
isolation domain. The analog: in files that use MemoizR (a `using` directive for the MemoizR
namespaces — the rule's mandate boundary), a static must be an **immutable slot of a Sendable
type**. Flagged: non-readonly static fields, settable static properties, static events (mutable
slots), and readonly/get-only statics whose TYPE is not Sendable (one shared mutable object
graph). Not flagged: consts, computed static getters (an expression-bodied getter owns no slot
— fresh values share nothing, and a getter handing out other static state is flagged at that
state's own declaration), Sendable readonly statics — and MemoizR's own nodes, factories and
executors, which are `[Sendable]` by design (and all sealed), so the rule's own fix suggestion
("lift it into a Signal") passes the rule. A static slot that PASSES the rule but whose type
contains a smuggle surface (`static readonly OpenBase Cache` can store a mutable subclass, with
no creation site where MZR006 would hint and no runtime write validation ever seeing the slot)
gets the MZR006 Info hint at the static, with the same noise calculus as creation sites. A static whose type contains an unbound type parameter — `T` itself,
or nested as in `ImmutableArray<T>` — is flagged as unverifiable: MZR001's benefit of the doubt
relies on the closed instantiation being checked at its own creation site, and a static has no
such site — every closed `C<T>` mints a fresh process-wide slot no rule ever sees again.
`[Sendable]`-trusted types shield their arguments (a `Signal<T>` static is internally
synchronized for any `T`, and the closed `T` is checked at the `CreateSignal` call that built
the instance). The whole analyzer stays silent in compilations that do not reference the real
MemoizR assembly.

### MZR005 — use after transfer (the SE-0430 analog)

`Sending<T>` hands a non-Sendable value across flows by TRANSFER: the wrapper is `[Sendable]`
(strict mode accepts `Sending<List<int>>`), `Receive()` enforces single consumption at runtime,
and MZR005 flags method-local uses of the transferred variable after the transfer, in source
order, stopping at a reassignment. The scan is path-aware within its heuristic: uses in a
mutually exclusive sibling arm of the transfer's construct are unreachable and not flagged,
uses dominated by a conditional reinitialization (inside its arm, after it) are clean, `out`
arguments reinitialize like assignments (after their sibling arguments — which are evaluated
first — were checked for reads), reinitializations inside deferred callbacks don't count for
the outer flow (mirroring transfers being scoped to their own callback body), and a `finally`
arm counts as definite. Still deliberately a heuristic — Swift proves this with region-based
isolation in the type system; source order approximates execution order, and aliases or loop
back-edges can evade the rule. The receiver-side runtime check is the backstop.

Known blind spots of the heuristic (documented non-goals, each backstopped by
`Sending<T>.Receive`'s single-consumption check):

- aliases the sender keeps through another path — a `ref` local, a `dynamic` view, a copy taken
  before the transfer, or state reached through a field or property (only locals and parameters
  are transfer sources; delegate invocation lists resolve from same-scope stores only);
- loop back-edges: an iteration's use that textually precedes the transfer is not flagged for the
  next iteration;
- framework calls are classified by shape — `Create` factories carry their arguments, copiers
  (`CreateRange`, `ToImmutable*`, LINQ materializers) copy inline elements only, and interface-
  or view-returning methods retain their receiver — so a retaining method that fits none of
  these (`Task.FromResult(list)`) is a leaf;
- a callee that cannot return after its handoff is judged one level deep, and a declared-Sendable
  but non-sealed source stays tracked because its runtime object may be a mutable subclass.

### MZR006 — subclass smuggling (Info)

Sendable verdicts are computed from the DECLARED type, so a mutable subclass behind an upcast
passes creation-time checks — ADR 0003's documented limitation, which Swift closes by requiring
Sendable classes to be `final`. MZR006 hints (Info severity: non-sealed records are idiomatic,
and the hole needs an actual mutable subclass to bite) at non-sealed, non-abstract class type
arguments at creation sites; green-listed framework types (`Uri` is not sealed) are exempt.
Abstract classes and interfaces are normally MZR001's (Error) territory — except when a
`[Sendable]` assertion lets them pass: the attribute is deliberately not inherited, so the
assertion binds the declaring author and not every subclass or implementer, and MZR006 hints
exactly there. The runtime counterpart is `MemoFactoryOptions.ValidateWrittenValues`, which validates each written
instance's runtime type on `Set` — SIGNAL writes only (memo outputs are the computation's own
doing and publish unchecked), which is why the MZR006 hint only suggests the option at signal
creation sites. The nested type arguments of Sendable containers (`ImmutableArray<OpenBase>`)
are unfolded: the container passes the green-lists, the element type is the smuggle surface —
and since `ValidateWrittenValues` sees only the written instance's OWN runtime type, the hint
for a nested surface says the runtime guard cannot reach it instead of suggesting the option.
Creations on a factory that visibly opts out (`DisableSendableChecks`, see MZR001) get no hint
at all: smuggling is a hole in checks that factory disabled. `[Sendable]`-attributed types
shield their type arguments and members from the walk — `Sending<T>` deliberately wraps a
non-Sendable payload for transfer, so hinting about the payload would misread the escape hatch
— and the walk has no depth cap (the type graph is finite and a visited set breaks
self-referential cycles; a cap would silently drop the hint exactly for the deep compositions
MZR001 accepts). Besides generic type arguments, the walk visits the MEMBER types of
source-declared types: a sealed Sendable DTO (`sealed record Box(OpenBase Value)`) hides the
same hole one member deep, where `ValidateWrittenValues` sees only the runtime type `Box`.

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
- **Error severity by default.** Rejected at v1 as the Swift-5.x-style migration step, then
  adopted for MZR001–003 by issue #145 part A4 alongside the runtime default-on switch (the
  Swift 6 posture); `.editorconfig` downgrades and the `DisableSendableChecks` factory opt-out
  (honored by the analyzers where the construction is visible) are the migration path.
