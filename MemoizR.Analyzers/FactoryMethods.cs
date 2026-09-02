using System.Linq;
using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers;

// Classification of the MemoizR factory methods the rules hook into. Matching is by containing
// type + method name (extension methods arrive reduced; ContainingType is still the static
// class) PLUS library-assembly identity, so the analyzer needs no reference to MemoizR itself
// and a source-shadowed lookalike factory cannot draw the diagnostics onto unrelated APIs.
internal static class FactoryMethods
{
    // Methods whose generic type arguments are value types the graph shares across flows
    // (MZR001). Mirrors the runtime strict-mode enforcement points: every value-bearing node,
    // including ConcurrentRace's resolver result R (checked because TypeArguments are checked
    // uniformly). Reactions are absent for the same reason as at runtime: they store no new
    // value type -- their sources were checked where they were created.
    public static bool IsValueBearingCreation(IMethodSymbol method)
    {
        return IsMemoFactoryMethod(method, "CreateSignal", "CreateEagerRelativeSignal", "CreateMemoizR", "CreateActorSignal", "CreateActorMemoizR")
            || IsStructuredFactoryMethod(method, "CreateConcurrentMap", "CreateConcurrentMapReduce", "CreateConcurrentRace")
            // ADR 0007's process layer: CreateAction's TPayload crosses to the detached run
            // flow, CreateOptimistic's T is the value its composed view publishes -- both are
            // runtime-checked in strict mode, so they get the same build-time mirror.
            || IsReactiveMemoFactoryMethod(method, "CreateAction", "CreateOptimistic");
    }

    // Methods whose delegate arguments execute inside graph evaluations, possibly concurrently
    // with everything else (MZR002, and the nested-walk pruning in ComputationLambdas).
    public static bool IsComputationHost(IMethodSymbol method)
    {
        return IsMemoFactoryMethod(method, "CreateMemoizR", "CreateActorMemoizR")
            || IsStructuredFactoryMethod(method, "CreateConcurrentMap", "CreateConcurrentMapReduce", "CreateConcurrentRace")
            || IsReactionBuilderMethod(method, "CreateReaction", "CreateAdvancedReaction")
            // The factory-level CreateReaction sugar forwards to BuildReaction().CreateReaction,
            // so its action is just as much a reaction computation -- without this, the
            // README-style f.CreateReaction(..) overloads bypass MZR002 entirely.
            || IsReactiveMemoFactoryMethod(method, "CreateReaction")
            // An optimistic PATCH is engine-executed: it is stored in the overlay and re-run by
            // the view's computation on whichever flow pulls, so a captured-state write in one
            // is exactly MZR002's data race. (Action BODIES are deliberately NOT hosts -- they
            // are user-driven process code; see ADR 0007.)
            || IsOptimisticContextMethod(method, "Apply");
    }

    // Hosts whose computations deterministically throw on a Set of their own graph (MZR003): in
    // the lock engine because the flow holds the evaluation lock in upgradeable mode, in the
    // actor engine because ActorSignal.Set rejects flows with an active capture frame.
    // ConcurrentMap children DO run on forced fresh scopes (StructuredResultsJob.ForceNewScope)
    // and are excluded; ConcurrentMapReduce and ConcurrentRace children share their parent flow's
    // scope (StructuredRaceJob does not force scopes, and its resolver runs on the parent flow),
    // so they are included.
    public static bool IsSameFlowEvaluationHost(IMethodSymbol method)
    {
        return IsMemoFactoryMethod(method, "CreateMemoizR", "CreateActorMemoizR")
            || IsStructuredFactoryMethod(method, "CreateConcurrentMapReduce", "CreateConcurrentRace")
            || IsReactionBuilderMethod(method, "CreateReaction", "CreateAdvancedReaction")
            || IsReactiveMemoFactoryMethod(method, "CreateReaction")
            // A patch runs inside the view memo's recompute, whose flow holds the evaluation
            // lock -- a Set in one throws exactly like a Set in the memo's own computation.
            || IsOptimisticContextMethod(method, "Apply");
    }

    // Whether the delegate argument is an OPTIMISTIC PATCH (MZR004's subject, and a computation
    // for MZR002/MZR003 via the classifications above).
    public static bool IsOptimisticPatchHost(IMethodSymbol method)
    {
        return IsOptimisticContextMethod(method, "Apply");
    }

    // Whether the host runs an ACTOR-engine computation (CreateActorMemoizR). MZR003 uses this to
    // match the Set to the host's engine: only ActorSignal.Set throws inside an actor computation
    // (it rejects flows with an active ActorFlow.Frame), while only lock-engine Signal/
    // EagerRelativeSignal.Set throws inside a lock-engine computation (exclusive-inside-upgradeable).
    // A cross-engine Set (e.g. Signal.Set inside CreateActorMemoizR) does NOT throw, so flagging it
    // would be a false positive.
    public static bool IsActorEngineHost(IMethodSymbol method)
    {
        return IsMemoFactoryMethod(method, "CreateActorMemoizR");
    }

    private static bool IsMemoFactoryMethod(IMethodSymbol method, params string[] names)
    {
        return IsOn(method, "MemoizR", "MemoFactory", names);
    }

    private static bool IsStructuredFactoryMethod(IMethodSymbol method, params string[] names)
    {
        return IsOn(method, "MemoizR", "StructuredConcurrencyFactory", names);
    }

    private static bool IsReactionBuilderMethod(IMethodSymbol method, params string[] names)
    {
        return IsOn(method, "MemoizR.Reactive", "ReactionBuilder", names);
    }

    // The factory-level reaction sugar (f.CreateReaction(...)) lives on this extension class.
    private static bool IsReactiveMemoFactoryMethod(IMethodSymbol method, params string[] names)
    {
        return IsOn(method, "MemoizR", "ReactiveMemoFactory", names);
    }

    // The action-run context an optimistic patch is applied through (ADR 0007).
    private static bool IsOptimisticContextMethod(IMethodSymbol method, params string[] names)
    {
        return IsOn(method, "MemoizR.Reactive", "OptimisticActionContext", names);
    }

    private static bool IsOn(IMethodSymbol method, string namespaceName, string typeName, string[] names)
    {
        var type = method.ContainingType;
        if (type is null || type.Name != typeName || type.ContainingNamespace?.ToDisplayString() != namespaceName
            || !IsLibraryType(type))
        {
            return false;
        }

        foreach (var name in names)
        {
            if (method.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    // A source-shadowed lookalike (a project's own MemoizR.MemoFactory, which source-wins over
    // the referenced one) must not be classified as the reactive factory: its APIs publish
    // nothing into a MemoizR graph, so firing MZR001-003 on them would be pure false positives
    // (a build break under warnings-as-errors). Kept in lockstep with the identity checks on
    // the SendableAttribute and the green-lists in SendableSymbolClassifier.
    internal static bool IsLibraryType(INamedTypeSymbol type)
    {
        if (type.Locations.Any(location => location.IsInSource))
        {
            return false;
        }

        return type.ContainingAssembly?.Identity.Name is
            "MemoizR" or "MemoizR.Reactive" or "MemoizR.StructuredConcurrency";
    }
}
