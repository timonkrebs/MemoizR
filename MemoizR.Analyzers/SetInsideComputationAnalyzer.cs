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
                        SendableSymbolClassifier.Display(inner.TargetMethod.ContainingType)));
                }
            }
        }
    }

    // A cross-FACTORY lock-engine Set does not throw at runtime: the Set locks the target
    // signal's own context, where the computing graph holds nothing. Skipped only when BOTH the
    // host's factory and the Set target's creating factory resolve and provably differ --
    // anything unprovable (a field signal wired in a constructor, a parameter) keeps the
    // diagnostic, because the overwhelmingly common case is one factory and the runtime
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
        return hostFactory is not null
            && targetFactory is not null
            && !SymbolEqualityComparer.Default.Equals(hostFactory, targetFactory);
    }

    // A Set that throws in THIS host's engine: ActorSignal.Set inside an actor computation, or
    // lock-engine Signal/EagerRelativeSignal.Set inside a lock-engine computation. A cross-engine
    // Set takes no same-flow lock and does not throw, so it must not be flagged.
    private static bool IsSameEngineSet(IMethodSymbol method, bool actorHost)
    {
        if (method.Name != "Set")
        {
            return false;
        }

        var type = method.ContainingType?.OriginalDefinition;
        if (type is not { Arity: 1 } || type.ContainingNamespace?.ToDisplayString() != "MemoizR")
        {
            return false;
        }

        return actorHost
            ? type.Name == "ActorSignal"
            : type.Name is "Signal" or "EagerRelativeSignal";
    }
}
