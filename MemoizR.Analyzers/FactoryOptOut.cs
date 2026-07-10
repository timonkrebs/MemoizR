using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        return PeelFluentCalls(ReceiverOf(invocation)) switch
        {
            IObjectCreationOperation creation => OptsOut(creation),
            ILocalReferenceOperation local => SymbolOptsOut(local.Local, invocation),
            IFieldReferenceOperation { Field.IsReadOnly: true } field
                => ContainingTypeFullyInSight(field.Field, invocation) && SymbolOptsOut(field.Field, invocation),
            // Overridable getters dispatch dynamically: a derived override can hand back a
            // strict factory the base initializer never saw, so only a getter that cannot
            // dispatch elsewhere may vouch for its initializer.
            IPropertyReferenceOperation { Property: { SetMethod: null, IsVirtual: false, IsAbstract: false, IsOverride: false } } property
                => ContainingTypeFullyInSight(property.Property, invocation) && SymbolOptsOut(property.Property, invocation),
            _ => false,
        };
    }

    // Fluent configuration (AddExecutor/AddTimeProvider/...) mutates and returns the SAME
    // factory, so the opt-out evidence sits one hop (or several) up the chain: follow
    // library-declared fluent calls to their own receiver. Applied to direct receivers AND to
    // initializer values (`var lax = new MemoFactory(…).AddExecutor(…);` stores the fluent
    // call's result, which IS the factory the initializer created).
    private static IOperation? PeelFluentCalls(IOperation? operation)
    {
        var current = Unwrap(operation);
        while (current is IInvocationOperation chained && ReturnsItsOwnFluentReceiver(chained.TargetMethod))
        {
            current = Unwrap(ReceiverOf(chained));
        }

        return current;
    }

    // Instance creations carry the factory in Instance; the structured-concurrency creations
    // and the fluent configuration methods are extension methods, whose receiver arrives as
    // the first argument.
    private static IOperation? ReceiverOf(IInvocationOperation invocation)
    {
        return invocation.Instance
            ?? (invocation.TargetMethod.IsExtensionMethod
                ? invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value
                : null);
    }

    // The fluent-configuration contract is a NAMED whitelist: these methods mutate and return
    // their receiver. Return-type matching alone would also follow generic passthroughs --
    // Untrack<T> returns its DELEGATE's result, which with T = MemoFactory can be any factory
    // -- so the name set is the authority and the shape checks (non-generic, library-declared,
    // MemoFactory-returning) are the belt; a source-declared lookalike is not followed either.
    private static bool ReturnsItsOwnFluentReceiver(IMethodSymbol method)
    {
        return method.Name is "AddExecutor" or "AddSynchronizationContext" or "AddTimeProvider" or "AddWpfDispatcher"
            && method.Arity == 0
            && method.ReturnType is INamedTypeSymbol { Name: "MemoFactory" } factoryType
            && factoryType.ContainingNamespace?.ToDisplayString() == "MemoizR"
            && FactoryMethods.IsLibraryType(factoryType)
            && method.ContainingType is { } containingType
            && FactoryMethods.IsLibraryType(containingType);
    }

    // A readonly field's (or get-only auto-property's) initializer can still be overwritten by
    // a constructor, and a partial type can keep that constructor in ANOTHER file, outside the
    // reassignment scan's reach: only members of types declared entirely in the invocation's
    // file are trusted.
    private static bool ContainingTypeFullyInSight(ISymbol symbol, IInvocationOperation invocation)
    {
        var tree = invocation.SemanticModel?.SyntaxTree;
        return tree is not null
            && symbol.ContainingType is { } containingType
            && containingType.DeclaringSyntaxReferences.All(reference => reference.SyntaxTree == tree);
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

            if (initializer is not null && PeelFluentCalls(model.GetOperation(initializer)) is IObjectCreationOperation creation)
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
    // including through a member access or a deconstruction tuple) or escapes by reference --
    // a ref/out argument, or `ref x` in a ref-local declaration / ref reassignment / ref
    // return (RefExpressionSyntax), through which any later write repoints the symbol without
    // naming it.
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
            // `in` stays a read: the callee cannot repoint the symbol through a read-only pass.
            ArgumentSyntax argument => argument.RefOrOutKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword,
            RefExpressionSyntax => true,
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
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                // `lax?.CreateSignal(...)`: the invocation's receiver is the conditional-access
                // PLACEHOLDER; the factory is the enclosing conditional access's Operation
                // (`lax`). At runtime the creation either does not run or runs on that factory
                // -- either way the opt-out evidence lives there.
                case IConditionalAccessInstanceOperation placeholder:
                    operation = EnclosingConditionalAccess(placeholder)?.Operation;
                    continue;
                default:
                    return operation;
            }
        }
    }

    private static IConditionalAccessOperation? EnclosingConditionalAccess(IOperation placeholder)
    {
        for (var parent = placeholder.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IConditionalAccessOperation conditional)
            {
                return conditional;
            }
        }

        return null;
    }
}
