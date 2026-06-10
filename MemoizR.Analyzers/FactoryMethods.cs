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
        return IsMemoFactoryMethod(method, "CreateSignal", "CreateEagerRelativeSignal", "CreateMemoizR")
            || IsStructuredFactoryMethod(method, "CreateConcurrentMap", "CreateConcurrentMapReduce", "CreateConcurrentRace");
    }

    // Methods whose delegate arguments execute inside graph evaluations, possibly concurrently
    // with everything else (MZR002, and the nested-walk pruning in ComputationLambdas).
    public static bool IsComputationHost(IMethodSymbol method)
    {
        return IsMemoFactoryMethod(method, "CreateMemoizR")
            || IsStructuredFactoryMethod(method, "CreateConcurrentMap", "CreateConcurrentMapReduce", "CreateConcurrentRace")
            || IsReactionBuilderMethod(method, "CreateReaction", "CreateAdvancedReaction");
    }

    // Hosts whose computations run while their OWN flow holds the evaluation lock in upgradeable
    // mode, so a Signal.Set inside them deterministically throws (MZR003). ConcurrentMap and
    // ConcurrentRace children run on forced fresh scopes and are deliberately excluded;
    // ConcurrentMapReduce children share their parent flow's scope, so they are included.
    public static bool IsSameFlowEvaluationHost(IMethodSymbol method)
    {
        return IsMemoFactoryMethod(method, "CreateMemoizR")
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
