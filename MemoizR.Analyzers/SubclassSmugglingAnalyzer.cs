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

    // Non-sealed concrete classes smuggle via ordinary upcasts. Abstract classes and
    // interfaces are normally MZR001's (Error) territory -- EXCEPT when a [Sendable] assertion
    // lets them pass: the attribute is deliberately not inherited (each type must make the
    // promise for itself), so the assertion binds the declaring author and NOT every subclass
    // or implementer -- the smuggle hole reopens exactly where the Error rule went quiet.
    // Green-listed framework types (Uri is not sealed!) are not plausibly subclassed by
    // accident, object stays MZR001's, and value types cannot be subclassed at all.
    private static bool IsSmuggleSurface(INamedTypeSymbol named)
    {
        if (named.SpecialType == SpecialType.System_Object || SendableSymbolClassifier.IsFrameworkGreenListed(named))
        {
            return false;
        }

        return named.TypeKind switch
        {
            TypeKind.Class when !named.IsAbstract => !named.IsSealed,
            TypeKind.Class => SendableSymbolClassifier.HasSendableAttribute(named),
            TypeKind.Interface => SendableSymbolClassifier.HasSendableAttribute(named),
            _ => false,
        };
    }

    private static bool IsSignalCreation(IMethodSymbol method)
    {
        return method.Name is "CreateSignal" or "CreateEagerRelativeSignal" or "CreateActorSignal";
    }
}
