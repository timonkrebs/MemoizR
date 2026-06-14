using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// MZR002, the SE-0412 analog: a reactive computation that WRITES state it shares with code
// outside itself (a captured local/parameter, a field of the enclosing object, a static field)
// is unsynchronized shared mutation -- computations run concurrently on other flows. Reads are
// deliberately not flagged: read-only captured configuration is idiomatic, and proving a read
// races requires whole-program knowledge an analyzer does not have. Mutations through a captured
// reference (`capturedList.Add(1)`) are likewise out of scope here; that is MZR001's territory
// (the value type should be Sendable). The suggested fix is the library's own model: lift the
// state into a Signal/EagerRelativeSignal.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapturedMutationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.CapturedMutation);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!FactoryMethods.IsComputationHost(invocation.TargetMethod))
        {
            return;
        }

        foreach (var lambda in ComputationLambdas.OfInvocation(invocation))
        {
            foreach (var operation in ComputationLambdas.Descend(lambda.Body))
            {
                foreach (var target in MutationTargets(operation))
                {
                    ReportIfShared(context, target, lambda);
                }
            }
        }
    }

    private static ImmutableArray<IOperation> MutationTargets(IOperation operation)
    {
        switch (operation)
        {
            case ISimpleAssignmentOperation assignment:
                return ImmutableArray.Create(assignment.Target);
            case ICompoundAssignmentOperation compound:
                return ImmutableArray.Create(compound.Target);
            case ICoalesceAssignmentOperation coalesce:
                return ImmutableArray.Create(coalesce.Target);
            case IIncrementOrDecrementOperation increment:
                return ImmutableArray.Create(increment.Target);
            case IDeconstructionAssignmentOperation deconstruction when deconstruction.Target is ITupleOperation tuple:
                return tuple.Elements;
            case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument:
                return ImmutableArray.Create(argument.Value);
            default:
                return ImmutableArray<IOperation>.Empty;
        }
    }

    private static void ReportIfShared(OperationAnalysisContext context, IOperation target, IAnonymousFunctionOperation lambda)
    {
        var (kind, symbol) = target switch
        {
            ILocalReferenceOperation local when IsDeclaredOutside(local.Local, lambda)
                => ("captured local", (ISymbol)local.Local),
            IParameterReferenceOperation parameter when IsDeclaredOutside(parameter.Parameter, lambda)
                => ("captured parameter", parameter.Parameter),
            IFieldReferenceOperation { Field.IsStatic: true } staticField
                => ("static field", staticField.Field),
            IFieldReferenceOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } } instanceField
                => ("field", instanceField.Field),
            _ => (null, null!),
        };

        if (kind is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.CapturedMutation,
            target.Syntax.GetLocation(),
            kind,
            symbol.Name));
    }

    // "Captured" = declared outside this computation lambda's syntax. The span test (rather than
    // comparing containing symbols) is what keeps nested non-computation lambdas correct: a local
    // declared in a nested LINQ lambda belongs to the computation and must not be flagged, while
    // a local of the enclosing method is shared and must be.
    private static bool IsDeclaredOutside(ISymbol symbol, IAnonymousFunctionOperation lambda)
    {
        var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null)
        {
            // Compiler-generated (e.g. a setter's value parameter): nothing actionable to point at.
            return false;
        }

        return declaration.SyntaxTree != lambda.Syntax.SyntaxTree
            || !lambda.Syntax.Span.Contains(declaration.Span);
    }
}
