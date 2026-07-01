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
        defaultSeverity: DiagnosticSeverity.Warning,
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
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A memo/reaction/concurrent computation executes on arbitrary flows, concurrently with " +
                     "the code that created it and with other computations. Writing a captured local, a field " +
                     "of the enclosing object, or a static field from inside one is unsynchronized shared " +
                     "mutation — the very thing the reactive graph exists to replace (the SE-0412 analog).",
        helpLinkUri: HelpUri);

    public static readonly DiagnosticDescriptor SetInsideComputation = new(
        id: "MZR003",
        title: "Signal.Set inside a reactive computation throws at runtime",
        messageFormat: "'{0}.Set' is called inside a reactive computation; the computation's flow already holds " +
                       "the evaluation lock in upgradeable mode, so this exclusive acquisition throws " +
                       "InvalidOperationException at runtime — return the value instead, or schedule the write " +
                       "outside the evaluation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The evaluation lock deliberately converts this impossible same-flow wait into an " +
                     "exception (a write inside a read of the same graph is a feedback loop). This rule turns " +
                     "that runtime exception into a build-time diagnostic.",
        helpLinkUri: HelpUri);
}
