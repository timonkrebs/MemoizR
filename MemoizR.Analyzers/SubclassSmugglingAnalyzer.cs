using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
        // The classifier caches verdicts per type symbol, so it is scoped to one compilation.
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

        // Smuggling is a hole in the Sendable checks; a factory that visibly opted out of them
        // (DisableSendableChecks) has nothing to smuggle past, so the hint would be pure noise.
        if (FactoryOptOut.DisablesSendableChecks(invocation))
        {
            return;
        }

        foreach (var typeArgument in method.TypeArguments)
        {
            // A type argument the Sendable classifier REJECTS is MZR001's territory: the
            // value never passes the creation check, so there is no smuggle hole to hint at.
            if (classifier.GetNotSendableReason(typeArgument) is not null)
            {
                continue;
            }

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

    internal static IEnumerable<(INamedTypeSymbol Named, int Depth)> NamedTypesIn(ITypeSymbol type, int depth)
    {
        return NamedTypesIn(
            type,
            depth,
            new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default),
            new List<INamedTypeSymbol>());
    }

    private static IEnumerable<(INamedTypeSymbol Named, int Depth)> NamedTypesIn(
        ITypeSymbol type, int depth, HashSet<ITypeSymbol> visited,
        List<INamedTypeSymbol> path)
    {
        if (type is not INamedTypeSymbol named || !visited.Add(type))
        {
            yield break;
        }

        yield return (named, depth);

        // [Sendable]-attributed types shield their arguments and members: the thread-safety
        // assertion does not rest on them -- Sending<T> DELIBERATELY wraps a non-Sendable
        // payload for transfer, and a user-asserted container accounts for its contents. No
        // depth cap otherwise: the type graph is finite and the visited set breaks cycles
        // (self-referential records), so the walk terminates on its own.
        if (SendableSymbolClassifier.HasSendableAttribute(named))
        {
            yield break;
        }

        foreach (var argument in named.TypeArguments)
        {
            foreach (var nested in NamedTypesIn(argument, depth + 1, visited, path))
            {
                yield return nested;
            }
        }

        if (SendableSymbolClassifier.IsDivergentReinstantiation(named, path))
        {
            yield break;
        }

        path.Add(named);
        try
        {
            foreach (var memberType in SendableSymbolClassifier.StoredInstanceMemberTypesOf(named))
            {
                foreach (var nested in NamedTypesIn(memberType, depth + 1, visited, path))
                {
                    yield return nested;
                }
            }
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    // Non-sealed concrete classes smuggle via ordinary upcasts. Abstract classes and
    // interfaces are normally MZR001's (Error) territory -- EXCEPT when a [Sendable] assertion
    // lets them pass: the attribute is deliberately not inherited (each type must make the
    // promise for itself), so the assertion binds the declaring author and NOT every subclass
    // or implementer -- the smuggle hole reopens exactly where the Error rule went quiet.
    // Green-listed framework types (Uri is not sealed!) are not plausibly subclassed by
    // accident, object stays MZR001's, and value types cannot be subclassed at all. The
    // exception is a surface whose green-listed GENERIC subclass the framework itself ships
    // (Task <- Task<TResult>): no accident needed -- the creation check sees only the
    // declared type, and ValidateWrittenValues is the net that catches the instance.
    internal static bool IsSmuggleSurface(INamedTypeSymbol named)
    {
        if (named.IsValueType || named is { TypeKind: TypeKind.Class, IsAbstract: false, IsSealed: true }
            || named.SpecialType == SpecialType.System_Object
            || (SendableSymbolClassifier.IsFrameworkGreenListed(named)
                && !SendableSymbolClassifier.HasAGreenListedGenericSubclass(named)))
        {
            return false;
        }

        return named.TypeKind switch
        {
            TypeKind.Class when !named.IsAbstract => !named.IsSealed,
            TypeKind.Class or TypeKind.Interface => SendableSymbolClassifier.HasSendableAttribute(named),
            _ => false,
        };
    }

    private static bool IsSignalCreation(IMethodSymbol method)
    {
        return FactoryMethods.IsSignalCreation(method);
    }
}
