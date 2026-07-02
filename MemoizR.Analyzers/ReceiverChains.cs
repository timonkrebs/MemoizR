using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// Best-effort resolution of the FACTORY a creation hangs off: `f.CreateMemoizR(...)` -> f,
// `f.BuildReaction().CreateReaction(...)` -> f, `var b = f.BuildReaction(); b.CreateReaction`
// -> f, and -- for a node reference -- through the variable's same-tree initializer to the
// creation and on to its factory. Used by MZR003 to compare the Set target's factory with the
// computation host's: null means "unprovable", and callers must treat it as such.
internal static class ReceiverChains
{
    public static ISymbol? ResolveFactorySymbol(IInvocationOperation invocation, SemanticModel? semanticModel)
    {
        return ResolveReceiverSymbol(ReceiverOf(invocation), semanticModel, depth: 0);
    }

    // A node reference (the signal a Set is invoked on) resolves through its same-tree
    // initializer to the creating invocation, then to that creation's factory.
    public static ISymbol? ResolveCreatingFactorySymbol(IOperation? nodeReference, SemanticModel? semanticModel)
    {
        var creation = InitializerOf(SymbolOf(nodeReference), semanticModel);
        return creation is IInvocationOperation invocation
            ? ResolveFactorySymbol(invocation, semanticModel)
            : null;
    }

    // Extension hosts (the structured-concurrency creations, factory-level CreateReaction)
    // carry their receiver as argument 0 instead of Instance.
    private static IOperation? ReceiverOf(IInvocationOperation invocation)
    {
        if (invocation.Instance is { } instance)
        {
            return instance;
        }

        return invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0
            ? invocation.Arguments[0].Value
            : null;
    }

    private static ISymbol? ResolveReceiverSymbol(IOperation? receiver, SemanticModel? semanticModel, int depth)
    {
        if (depth > 8 || receiver is null)
        {
            return null;
        }

        switch (receiver)
        {
            case IInvocationOperation chained:
                return ResolveReceiverSymbol(ReceiverOf(chained), semanticModel, depth + 1);
            case IConversionOperation conversion:
                return ResolveReceiverSymbol(conversion.Operand, semanticModel, depth + 1);
            case ILocalReferenceOperation or IFieldReferenceOperation or IParameterReferenceOperation or IPropertyReferenceOperation:
                // An intermediate (a stored ReactionBuilder, a factory alias) resolves through
                // its initializer; when that gives out, the reference symbol itself is the
                // identity -- two creations hanging off the same local/field/parameter share a
                // factory by construction.
                var symbol = SymbolOf(receiver);
                var initialized = InitializerOf(symbol, semanticModel);
                if (initialized is not null && ResolveReceiverSymbol(initialized, semanticModel, depth + 1) is { } through)
                {
                    return through;
                }

                return symbol;
            default:
                return null;
        }
    }

    private static ISymbol? SymbolOf(IOperation? reference)
    {
        return reference switch
        {
            ILocalReferenceOperation local => local.Local,
            IFieldReferenceOperation field => field.Field,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };
    }

    private static IOperation? InitializerOf(ISymbol? variable, SemanticModel? semanticModel)
    {
        if (variable is null || semanticModel is null)
        {
            return null;
        }

        var declaration = variable.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null
            || declaration.SyntaxTree != semanticModel.SyntaxTree
            || declaration.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initializer })
        {
            return null;
        }

        return semanticModel.GetOperation(initializer);
    }
}
