using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// MZR006: the Sendable verdict is computed from the DECLARED type, so a non-sealed class can
// smuggle a mutable subclass past the creation-time checks through an upcast -- ADR 0003's
// documented limitation, which Swift closes by requiring Sendable classes to be final. Info
// severity by design: non-sealed records are idiomatic and the hole only bites when an actual
// mutable subclass exists; the runtime counterpart is MemoFactoryOptions.ValidateWrittenValues,
// which checks each written instance's runtime type.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SubclassSmugglingAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.NonSealedValueType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!FactoryMethods.IsValueBearingCreation(method))
        {
            return;
        }

        // The runtime-guard suggestion only holds where the guard exists: ValidateWrittenValues
        // checks SIGNAL writes (values user code hands in); memo outputs publish unchecked.
        var mitigation = IsSignalCreation(method)
            ? ", or enable MemoFactoryOptions.ValidateWrittenValues to check written instances at runtime"
            : " (memo outputs publish unchecked: ValidateWrittenValues covers signal writes only)";

        foreach (var typeArgument in method.TypeArguments)
        {
            // Smuggling hides inside Sendable CONTAINERS too (ImmutableArray<OpenBase>,
            // Task<OpenBase>): the container passes the green-lists, but the non-sealed element
            // type is the smuggle surface -- unfold nested type arguments.
            foreach (var named in NamedTypesIn(typeArgument, depth: 0))
            {
                if (IsSmuggleSurface(named))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.NonSealedValueType,
                        ComputationLambdas.NameLocation(invocation),
                        SendableSymbolClassifier.Display(named),
                        mitigation));
                }
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<INamedTypeSymbol> NamedTypesIn(ITypeSymbol type, int depth)
    {
        if (depth > 4 || type is not INamedTypeSymbol named)
        {
            yield break;
        }

        yield return named;
        foreach (var argument in named.TypeArguments)
        {
            foreach (var nested in NamedTypesIn(argument, depth + 1))
            {
                yield return nested;
            }
        }
    }

    // Interfaces, abstract classes and object are MZR001's (Warning) territory already;
    // green-listed framework types (Uri is not sealed!) are not plausibly subclassed by
    // accident, and value types cannot be subclassed at all.
    private static bool IsSmuggleSurface(INamedTypeSymbol named)
    {
        return named.TypeKind == TypeKind.Class
            && !named.IsSealed
            && !named.IsAbstract
            && named.SpecialType != SpecialType.System_Object
            && !SendableSymbolClassifier.IsFrameworkGreenListed(named);
    }

    private static bool IsSignalCreation(IMethodSymbol method)
    {
        return method.Name is "CreateSignal" or "CreateEagerRelativeSignal" or "CreateActorSignal";
    }
}
