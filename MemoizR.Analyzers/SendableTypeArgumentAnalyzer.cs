using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// MZR001: the build-time mirror of MemoFactoryOptions.StrictSendableChecks. Every generic type
// argument of a value-bearing factory creation is classified by the same rules the runtime
// checker applies; a non-Sendable argument is reported where the node is created. Checking
// TypeArguments uniformly covers ConcurrentRace's resolver result R for free.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SendableTypeArgumentAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.NonSendableValueType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // The classifier caches verdicts per type symbol, so it must be scoped to one
        // compilation: symbols cached across compilations would be both wrong and a leak.
        context.RegisterCompilationStartAction(compilationStart =>
        {
            var classifier = new SendableSymbolClassifier();
            compilationStart.RegisterOperationAction(
                operationContext => Analyze(operationContext, classifier),
                OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, SendableSymbolClassifier classifier)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!FactoryMethods.IsValueBearingCreation(method))
        {
            return;
        }

        foreach (var typeArgument in method.TypeArguments)
        {
            var reason = classifier.GetNotSendableReason(typeArgument);
            if (reason is null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NonSendableValueType,
                ComputationLambdas.NameLocation(invocation),
                SendableSymbolClassifier.Display(typeArgument),
                reason));
        }
    }
}
