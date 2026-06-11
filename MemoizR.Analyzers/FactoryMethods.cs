using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers;

// Classification of the MemoizR factory methods the rules hook into. Matching is by containing
// type + method name (extension methods arrive reduced; ContainingType is still the static
// class), so the analyzer needs no reference to MemoizR itself.
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
            || IsStructuredFactoryMethod(method, "CreateConcurrentMap", "CreateConcurrentMapReduce", "CreateConcurrentRace");
    }

    // Methods whose delegate arguments execute inside graph evaluations, possibly concurrently
    // with everything else (MZR002, and the nested-walk pruning in ComputationLambdas).
    public static bool IsComputationHost(IMethodSymbol method)
    {
        return IsMemoFactoryMethod(method, "CreateMemoizR", "CreateActorMemoizR")
            || IsStructuredFactoryMethod(method, "CreateConcurrentMap", "CreateConcurrentMapReduce", "CreateConcurrentRace")
            || IsReactionBuilderMethod(method, "CreateReaction", "CreateAdvancedReaction");
    }

    // Hosts whose computations deterministically throw on a Set of their own graph (MZR003): in
    // the lock engine because the flow holds the evaluation lock in upgradeable mode, in the
    // actor engine because ActorSignal.Set rejects flows with an active capture frame.
    // ConcurrentMap and ConcurrentRace children run on forced fresh scopes and are deliberately
    // excluded; ConcurrentMapReduce children share their parent flow's scope, so they are
    // included.
    public static bool IsSameFlowEvaluationHost(IMethodSymbol method)
    {
        return IsMemoFactoryMethod(method, "CreateMemoizR", "CreateActorMemoizR")
            || IsStructuredFactoryMethod(method, "CreateConcurrentMapReduce")
            || IsReactionBuilderMethod(method, "CreateReaction", "CreateAdvancedReaction");
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

    private static bool IsOn(IMethodSymbol method, string namespaceName, string typeName, string[] names)
    {
        var type = method.ContainingType;
        if (type is null || type.Name != typeName || type.ContainingNamespace?.ToDisplayString() != namespaceName)
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
}
