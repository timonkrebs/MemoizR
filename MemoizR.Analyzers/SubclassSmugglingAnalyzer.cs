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
// which checks each written instance's runtime type -- the instance's OWN type only, so the
// hint suggests it solely where it can fire (top-level signal values, not nested contents).
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

        // Smuggling is a hole in the Sendable checks; a factory that visibly opted out of them
        // (DisableSendableChecks) has nothing to smuggle past, so the hint would be pure noise.
        if (FactoryOptOut.DisablesSendableChecks(invocation))
        {
            return;
        }

        foreach (var typeArgument in method.TypeArguments)
        {
            // Smuggling hides inside Sendable CONTAINERS too (ImmutableArray<OpenBase>,
            // Task<OpenBase>): the container passes the green-lists, but the non-sealed element
            // type is the smuggle surface -- unfold nested type arguments.
            foreach (var (named, depth) in NamedTypesIn(typeArgument, depth: 0))
            {
                if (IsSmuggleSurface(named))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.NonSealedValueType,
                        ComputationLambdas.NameLocation(invocation),
                        SendableSymbolClassifier.Display(named),
                        Mitigation(method, depth)));
                }
            }
        }
    }

    // The runtime-guard suggestion only holds where the guard can actually fire:
    // ValidateWrittenValues checks the WRITTEN INSTANCE's runtime type on SIGNAL writes. Memo
    // outputs publish unchecked, and a surface nested inside a container
    // (ImmutableArray<OpenBase>) is invisible to it -- the check sees the written container's
    // type, never the contents behind it.
    private static string Mitigation(IMethodSymbol method, int depth)
    {
        if (!IsSignalCreation(method))
        {
            return " (memo outputs publish unchecked: ValidateWrittenValues covers signal writes only)";
        }

        return depth == 0
            ? ", or enable MemoFactoryOptions.ValidateWrittenValues to check written instances at runtime"
            : " (ValidateWrittenValues cannot see it: the runtime check covers the written instance's own type, not contents nested inside it)";
    }

    private static System.Collections.Generic.IEnumerable<(INamedTypeSymbol Named, int Depth)> NamedTypesIn(ITypeSymbol type, int depth)
    {
        if (type is not INamedTypeSymbol named)
        {
            yield break;
        }

        yield return (named, depth);

        // [Sendable]-attributed types shield their arguments: the thread-safety assertion does
        // not rest on them -- Sending<T> DELIBERATELY wraps a non-Sendable payload for
        // transfer, and a user-asserted container accounts for its contents. No depth cap
        // otherwise: the declared type tree is finite, so the walk terminates on its own, and
        // a cap would silently drop the hint for deeply nested surfaces.
        if (SendableSymbolClassifier.HasSendableAttribute(named))
        {
            yield break;
        }

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
