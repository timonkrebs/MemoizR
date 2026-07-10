using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// The build-time side of MemoFactoryOptions.DisableSendableChecks (A4's migration escape
// hatch): a creation on a factory that VISIBLY opts out must not fail the very checks its
// runtime disabled, or the documented per-factory opt-out would be unusable under the Error
// default without a project-wide suppression. The exemption needs POSITIVE, DEFINITE evidence,
// because a wrong opt-out silently drops the rule while a missed one only costs a suppression:
//
//  - the factory's construction in sight: an inline receiver, or the initializer of a local,
//    readonly field, or get-only property in the same file (Compilation.GetSemanticModel is
//    banned in analyzers, so cross-file initializers are out of reach; settable slots could
//    be repointed from anywhere),
//  - an options argument that FOLDS TO A CONSTANT carrying the flag -- a conditional or
//    computed expression that merely mentions DisableSendableChecks might still evaluate
//    strict at runtime,
//  - and no reassignment of the receiver symbol anywhere in the file, so the initializer is
//    actually the value the creation runs on.
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

        return Unwrap(receiver) switch
        {
            IObjectCreationOperation creation => OptsOut(creation),
            ILocalReferenceOperation local => SymbolOptsOut(local.Local, invocation),
            IFieldReferenceOperation { Field.IsReadOnly: true } field => SymbolOptsOut(field.Field, invocation),
            IPropertyReferenceOperation { Property.SetMethod: null } property => SymbolOptsOut(property.Property, invocation),
            _ => false,
        };
    }

    private static bool SymbolOptsOut(ISymbol symbol, IInvocationOperation invocation)
    {
        var model = invocation.SemanticModel;
        return model is not null
            && InitializerCreation(symbol, model) is { } creation
            && OptsOut(creation)
            && !IsReassigned(symbol, model);
    }

    // The declaration initializer, resolvable only in the invocation's own tree: that tree's
    // semantic model is already in hand, and locals are same-tree by construction -- only a
    // factory field/property declared in ANOTHER file stays unresolved (and therefore checked).
    private static IObjectCreationOperation? InitializerCreation(ISymbol symbol, SemanticModel model)
    {
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

    // Any write to the symbol after its declaration revokes the initializer's authority: the
    // local may have been repointed at a strict factory before the creation (and a readonly
    // field's static constructor can overwrite its initializer). The scan is file-wide and
    // position-blind -- a loop's back-edge makes a source-later write execute before a
    // source-earlier creation, so ordering by position would trust too much. Runs only for
    // receivers whose initializer already proved an opt-out, so the binds stay rare.
    private static bool IsReassigned(ISymbol symbol, SemanticModel model)
    {
        foreach (var identifier in model.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != symbol.Name || !IsWriteContext(identifier))
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, symbol))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the identifier is the target of any assignment kind (simple/compound/coalesce,
    // including through a member access or a deconstruction tuple) or escapes by ref/out.
    private static bool IsWriteContext(IdentifierNameSyntax identifier)
    {
        SyntaxNode current = identifier;
        if (current.Parent is MemberAccessExpressionSyntax member && ReferenceEquals(member.Name, identifier))
        {
            current = member;
        }

        while (current.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax tuple })
        {
            current = tuple;
        }

        return current.Parent switch
        {
            AssignmentExpressionSyntax assignment => ReferenceEquals(assignment.Left, current),
            ArgumentSyntax argument => argument.RefOrOutKeyword.RawKind != 0,
            _ => false,
        };
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
            // declared value rather than hardcoding it. A NON-constant expression is never a
            // definite opt-out, however prominently it mentions the flag: a conditional like
            // `useLax ? DisableSendableChecks : None` still runs strict on one path.
            return argument.Value.ConstantValue is { HasValue: true, Value: int flags }
                && DisableFlag(optionsType) is { } bit
                && (flags & bit) != 0;
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
