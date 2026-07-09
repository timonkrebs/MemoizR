using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers;

// The compile-time half of issue #36's data-race safety (ADR 0004): what the runtime layer
// (SendableChecker / strict mode / the evaluation lock's deadlock-to-exception conversions)
// enforces when the program runs, these rules surface on every build.
internal static class DiagnosticDescriptors
{
    private const string Category = "Concurrency";

    private const string HelpUri =
        "https://github.com/timonkrebs/MemoizR/blob/main/docs/adr/0004-compile-time-data-race-diagnostics.md";

    public static readonly DiagnosticDescriptor NonSendableValueType = new(
        id: "MZR001",
        title: "Value type shared by the reactive graph is not Sendable",
        messageFormat: "'{0}' is not Sendable ({1}) — values of this type are shared across concurrently " +
                       "running flows; use an immutable type, or mark it [Sendable] to assert thread safety",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "MemoizR publishes a node's value reference tear-free across concurrent flows, but only " +
                     "an immutable or internally synchronized type makes the object behind the reference safe " +
                     "to share. This is the build-time mirror of MemoFactoryOptions.StrictSendableChecks.",
        helpLinkUri: HelpUri);

    public static readonly DiagnosticDescriptor CapturedMutation = new(
        id: "MZR002",
        title: "Reactive computation mutates state shared with code outside it",
        messageFormat: "This computation mutates {0} '{1}', which is shared with code outside the computation; " +
                       "computations run concurrently on other flows, so this is a data race — lift the state " +
                       "into a Signal or EagerRelativeSignal instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A memo/reaction/concurrent computation executes on arbitrary flows, concurrently with " +
                     "the code that created it and with other computations. Writing a captured local, a field " +
                     "of the enclosing object, or a static field from inside one is unsynchronized shared " +
                     "mutation — the very thing the reactive graph exists to replace (the SE-0412 analog).",
        helpLinkUri: HelpUri);

    public static readonly DiagnosticDescriptor MutableStaticState = new(
        id: "MZR004",
        title: "Static state shared with the reactive graph is not data-race safe",
        messageFormat: "Static {0} '{1}' is {2} — statics are reachable from every concurrently running " +
                       "flow (the SE-0412 analog); make it readonly with a Sendable type, lift it into a " +
                       "Signal/EagerRelativeSignal (nodes are safe to hold in statics), or mark the type [Sendable]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Swift 6 rejects every non-isolated mutable global (SE-0412) because globals are " +
                     "reachable from every isolation domain. The analog here: a mutable static slot, or a " +
                     "readonly static whose TYPE is mutable, is unsynchronized shared state next to a graph " +
                     "whose computations run concurrently. Scoped to files that use MemoizR (a using " +
                     "directive for the MemoizR namespaces).",
        helpLinkUri: HelpUri);

    public static readonly DiagnosticDescriptor UseAfterTransfer = new(
        id: "MZR005",
        title: "Value used after being transferred",
        messageFormat: "'{0}' is used after being wrapped in Sending<T> — the receiver may already own " +
                       "and mutate it on another flow; stop using a transferred value (or reassign it first)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Sending<T> hands a non-Sendable value across flows by TRANSFER (the SE-0430 analog): " +
                     "the sender promises to stop touching it. This rule flags method-local uses of the " +
                     "transferred variable after the transfer, in source order, stopping at a reassignment. " +
                     "It is a best-effort heuristic, not a proof: aliases and loop back-edges can evade it.",
        helpLinkUri: HelpUri);

    public static readonly DiagnosticDescriptor NonSealedValueType = new(
        id: "MZR006",
        title: "Non-sealed class shared by the reactive graph can smuggle mutable subclass state",
        messageFormat: "'{0}' is not sealed — a mutable subclass behind an upcast passes the creation-time " +
                       "Sendable checks (Swift requires Sendable classes to be final); consider sealing it, " +
                       "or enable MemoFactoryOptions.ValidateWrittenValues to check written instances at runtime",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Sendable verdicts are computed from the DECLARED type's structure, so a mutable " +
                     "subclass smuggled in through an upcast is not caught at creation time (ADR 0003's " +
                     "documented limitation). Swift closes this by requiring Sendable classes to be final. " +
                     "Info severity: non-sealed records are idiomatic and the hole needs an actual mutable " +
                     "subclass to bite.",
        helpLinkUri: HelpUri);

    public static readonly DiagnosticDescriptor SetInsideComputation = new(
        id: "MZR003",
        title: "A graph write inside a reactive computation throws at runtime",
        messageFormat: "'{0}.{1}' is called inside a reactive computation; the computation's flow already holds " +
                       "the evaluation lock in upgradeable mode, so this exclusive acquisition throws " +
                       "InvalidOperationException at runtime — return the value instead, or schedule the write " +
                       "outside the evaluation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The evaluation lock deliberately converts this impossible same-flow wait into an " +
                     "exception (a write inside a read of the same graph is a feedback loop). This rule turns " +
                     "that runtime exception into a build-time diagnostic.",
        helpLinkUri: HelpUri);
}
