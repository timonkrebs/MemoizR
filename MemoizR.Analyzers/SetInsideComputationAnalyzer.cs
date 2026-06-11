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

        foreach (var lambda in ComputationLambdas.OfInvocation(invocation))
        {
            foreach (var operation in ComputationLambdas.Descend(lambda.Body))
            {
                if (operation is IInvocationOperation inner && IsSignalSet(inner.TargetMethod))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.SetInsideComputation,
                        ComputationLambdas.NameLocation(inner),
                        SendableSymbolClassifier.Display(inner.TargetMethod.ContainingType)));
                }
            }
        }
    }

    private static bool IsSignalSet(IMethodSymbol method)
    {
        if (method.Name != "Set")
        {
            return false;
        }

        var type = method.ContainingType?.OriginalDefinition;
        return type is { Arity: 1, Name: "Signal" or "EagerRelativeSignal" or "ActorSignal" }
            && type.ContainingNamespace?.ToDisplayString() == "MemoizR";
    }
}
