using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// The build-time side of MemoFactoryOptions.DisableSendableChecks (A4's migration escape
// hatch): a creation on a factory that VISIBLY opts out must not fail the very checks its
// runtime disabled, or the documented per-factory opt-out would be unusable under the Error
// default without a project-wide suppression. Resolution is best-effort and conservative in
// the safe direction -- the factory's construction must be in sight (an inline receiver, or a
// local/field/property initializer in the same file; Compilation.GetSemanticModel is banned in
// analyzers, so cross-file initializers stay out of reach). Anything unresolvable (factory
// parameter, reassigned local, options computed elsewhere) keeps the checks ON: a missed
// opt-out costs one suppression, a wrong opt-out would silently drop the rule.
internal static class FactoryOptOut
{
    public static bool DisablesSendableChecks(IInvocationOperation invocation)
    {
        // Instance creations carry the factory in Instance; the structured-concurrency
        // creations are extension methods, whose receiver arrives as the first argument.
        var receiver = invocation.Instance
            ?? (invocation.TargetMethod.IsExtensionMethod
                ? invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value
                : null);

        return FactoryCreationBehind(Unwrap(receiver), invocation) is { } creation && OptsOut(creation);
    }

    private static IObjectCreationOperation? FactoryCreationBehind(IOperation? receiver, IInvocationOperation invocation)
    {
        return receiver switch
        {
            IObjectCreationOperation creation => creation,
            ILocalReferenceOperation local => InitializerCreation(local.Local, invocation),
            IFieldReferenceOperation field => InitializerCreation(field.Field, invocation),
            IPropertyReferenceOperation property => InitializerCreation(property.Property, invocation),
            _ => null,
        };
    }

    // The declaration initializer, resolvable only in the invocation's own tree: that tree's
    // semantic model is already in hand, and locals are same-tree by construction -- only a
    // factory field/property declared in ANOTHER file stays unresolved (and therefore checked).
    private static IObjectCreationOperation? InitializerCreation(ISymbol symbol, IInvocationOperation invocation)
    {
        var model = invocation.SemanticModel;
        if (model is null)
        {
            return null;
        }

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree != model.SyntaxTree)
            {
                continue;
            }

            var initializer = reference.GetSyntax() switch
            {
                VariableDeclaratorSyntax { Initializer.Value: { } value } => value, // locals and fields
                PropertyDeclarationSyntax { Initializer.Value: { } value } => value,
                _ => null,
            };

            if (initializer is not null && Unwrap(model.GetOperation(initializer)) is IObjectCreationOperation creation)
            {
                return creation;
            }
        }

        return null;
    }

    private static bool OptsOut(IObjectCreationOperation factoryCreation)
    {
        if (factoryCreation.Type is not INamedTypeSymbol { Name: "MemoFactory" } factoryType
            || factoryType.ContainingNamespace?.ToDisplayString() != "MemoizR"
            || !FactoryMethods.IsLibraryType(factoryType))
        {
            return false;
        }

        foreach (var argument in factoryCreation.Arguments)
        {
            if (argument.Parameter?.Type is not INamedTypeSymbol { TypeKind: TypeKind.Enum, Name: "MemoFactoryOptions" } optionsType
                || !FactoryMethods.IsLibraryType(optionsType))
            {
                continue;
            }

            // Flag combinations of the enum's members fold to a compile-time constant (an
            // omitted optional argument folds to None); test the bit against the member's own
            // declared value rather than hardcoding it.
            if (argument.Value.ConstantValue is { HasValue: true, Value: int flags })
            {
                return DisableFlag(optionsType) is { } bit && (flags & bit) != 0;
            }

            // Non-constant composition (a flags local OR-ed together): a reference to the
            // member anywhere inside the argument is still positive evidence of the opt-out.
            return argument.Value.DescendantsAndSelf().Any(operation =>
                operation is IFieldReferenceOperation { Field.Name: "DisableSendableChecks" } member
                && SymbolEqualityComparer.Default.Equals(member.Field.ContainingType, optionsType));
        }

        return false;
    }

    private static int? DisableFlag(INamedTypeSymbol optionsType)
    {
        return optionsType.GetMembers("DisableSendableChecks").OfType<IFieldSymbol>()
            .FirstOrDefault()?.ConstantValue as int?;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
