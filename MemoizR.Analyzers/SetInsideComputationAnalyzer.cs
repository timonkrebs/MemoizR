using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// MZR003: Signal.Set / EagerRelativeSignal.Set inside a computation whose flow holds the
// evaluation lock in upgradeable mode is an exclusive-inside-upgradeable acquisition, which the
// AsyncAsymmetricLock deliberately converts into an InvalidOperationException (a write inside a
// read of the same graph is a feedback loop, and waiting would deadlock). This rule surfaces
// that runtime exception at build time. Hosts whose children run on forced fresh scopes
// (ConcurrentMap, ConcurrentRace) are excluded -- see FactoryMethods.IsSameFlowEvaluationHost.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SetInsideComputationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.SetInsideComputation);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!FactoryMethods.IsSameFlowEvaluationHost(invocation.TargetMethod))
        {
            return;
        }

        var actorHost = FactoryMethods.IsActorEngineHost(invocation.TargetMethod);

        foreach (var computation in ComputationLambdas.OfInvocation(invocation))
        {
            // Direct execution path only: a Set inside a callback the computation merely BUILDS
            // (the diagnostic's suggested escape) runs later, off the evaluation's flow.
            foreach (var operation in ComputationLambdas.DescendDirectExecution(computation.Body))
            {
                if (operation is IInvocationOperation inner
                    && IsSameEngineSet(inner.TargetMethod, actorHost)
                    && !IsProvablyCrossFactory(invocation, inner, actorHost))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.SetInsideComputation,
                        ComputationLambdas.NameLocation(inner),
                        SendableSymbolClassifier.Display(inner.TargetMethod.ContainingType),
                        inner.TargetMethod.Name));
                }
            }
        }
    }

    // A cross-CONTEXT lock-engine Set does not throw at runtime: the Set locks the target
    // signal's own context, where the computing graph holds nothing. Skipped only when the
    // host's and the Set target's factories resolve to different symbols AND their CONTEXTS
    // provably differ -- two factory variables constructed with the same key share one context
    // (and its lock), so mere variable inequality proves nothing there. Anything unprovable (a
    // field signal wired in a constructor, a parameter, a non-constant key) keeps the
    // diagnostic, because the overwhelmingly common case is one shared context and the runtime
    // exception is deterministic there. Actor hosts are exempt from the check: ActorSignal.Set
    // rejects on the flow's frame, which any actor computation carries regardless of context.
    private static bool IsProvablyCrossFactory(IInvocationOperation host, IInvocationOperation setInvocation, bool actorHost)
    {
        if (actorHost)
        {
            return false;
        }

        var hostFactory = ReceiverChains.ResolveFactorySymbol(host, host.SemanticModel);
        var targetFactory = ReceiverChains.ResolveCreatingFactorySymbol(setInvocation.Instance, setInvocation.SemanticModel);
        if (hostFactory is null
            || targetFactory is null
            || SymbolEqualityComparer.Default.Equals(hostFactory, targetFactory))
        {
            return false;
        }

        var hostContext = ReceiverChains.ResolveFactoryContextKey(hostFactory, host.SemanticModel);
        var targetContext = ReceiverChains.ResolveFactoryContextKey(targetFactory, setInvocation.SemanticModel);
        if (!hostContext.Resolved || !targetContext.Resolved)
        {
            return false;
        }

        // An unkeyed instance owns a fresh context, so any pairing involving one is disjoint;
        // two keyed instances share exactly when their constant keys match.
        return hostContext.ContextKey is null
            || targetContext.ContextKey is null
            || !Equals(hostContext.ContextKey, targetContext.ContextKey);
    }

    // A write API that throws in THIS host's engine: ActorSignal.Set inside an actor
    // computation, or lock-engine Signal/EagerRelativeSignal.Set and MemoBase.Invalidate (the
    // ADR 0007 refresh, which takes the same exclusive lock as Set) inside a lock-engine
    // computation. A cross-engine write takes no same-flow lock and does not throw, so it must
    // not be flagged.
    private static bool IsSameEngineSet(IMethodSymbol method, bool actorHost)
    {
        var type = method.ContainingType?.OriginalDefinition;
        if (type is not { Arity: 1 } || type.ContainingNamespace?.ToDisplayString() != "MemoizR"
            || !FactoryMethods.IsLibraryType(type))
        {
            // A source-shadowed MemoizR.Signal<T> lookalike's Set takes no evaluation lock and
            // does not throw -- name matching alone must not claim it does (same identity rule
            // as the factory-host classification).
            return false;
        }

        if (actorHost)
        {
            return method.Name == "Set" && type.Name == "ActorSignal";
        }

        return (method.Name == "Set" && type.Name is "Signal" or "EagerRelativeSignal")
            || (method.Name == "Invalidate" && type.Name == "MemoBase");
    }
}
