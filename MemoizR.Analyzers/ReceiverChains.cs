using System.Collections.Generic;
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
    // initializer to the creating invocation, then to that creation's factory. An INLINE
    // creation (`f.CreateSignal(0).Set(1)`) is its own provenance and resolves directly, and a
    // variable-to-variable ALIAS (`var state = s0;`) resolves through initializers until a
    // creation or a dead end -- the visited set breaks initializer cycles.
    public static ISymbol? ResolveCreatingFactorySymbol(IOperation? nodeReference, SemanticModel? semanticModel)
    {
        var reference = nodeReference;
        var site = nodeReference?.Syntax;
        HashSet<ISymbol>? visited = null;
        while (true)
        {
            switch (reference)
            {
                case IConversionOperation conversion:
                    reference = conversion.Operand;
                    continue;
                case IInvocationOperation creation:
                    return ResolveKnownCreationFactory(creation, semanticModel);
                case ILocalReferenceOperation or IFieldReferenceOperation or IParameterReferenceOperation or IPropertyReferenceOperation:
                    visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    var symbol = SymbolOf(reference);
                    if (symbol is null || !visited.Add(symbol) || site is null || IsReassignedBefore(symbol, site, semanticModel))
                    {
                        return null;
                    }

                    var initializer = InitializerOf(symbol, semanticModel);
                    site = initializer?.Syntax ?? site;
                    reference = initializer;
                    continue;
                default:
                    return null;
            }
        }
    }

    // A node variable REASSIGNED where the assignment can execute before this READ no longer
    // proves provenance: the value may come from any factory, and a suppression resting on the
    // stale initializer would drop a diagnostic the runtime contradicts -- unprovable keeps
    // it. A later straight-line reassignment cannot change the value already read, so it stays
    // trusted; deconstruction targets are flattened, like MZR004's delegate scan. Same-tree
    // syntactic, like every resolution here; each alias link checks against the site where its
    // value is read (the previous link's initializer).
    private static bool IsReassignedBefore(ISymbol variable, SyntaxNode reference, SemanticModel? semanticModel)
    {
        if (semanticModel is null)
        {
            return false;
        }

        // The sole assignment standing in for a missing initializer is initialization, not a
        // rebind (`OptimisticState<int> state; state = f1.CreateOptimistic(...);` still proves
        // provenance -- the same assignment is what InitializerOf resolves through) -- but
        // only when it can actually RUN before this read: a future write proves nothing about
        // the value read here.
        var effectiveInitializer = ComputationLambdas.EffectiveInitializerAssignment(variable, semanticModel);
        if (effectiveInitializer is not null
            && !ComputationLambdas.CanExecuteBefore(effectiveInitializer, reference, variable, semanticModel))
        {
            return true;
        }

        foreach (var node in semanticModel.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (!ReferenceEquals(node, effectiveInitializer)
                && ComputationLambdas.ReassignmentTargets(node) is { } targets
                && targets.Any(target => ComputationLambdas.WritesVariable(target, variable, semanticModel))
                && ComputationLambdas.CanExecuteBefore(node, reference, variable, semanticModel))
            {
                return true;
            }
        }

        return false;
    }

    // Only a RECOGNIZED creation proves provenance: an arbitrary helper that merely RETURNS a
    // node (`f1.MakeState()`) says nothing about which factory created what it returns, so
    // resolving its receiver would claim f1 and enable a suppression the runtime contradicts.
    private static ISymbol? ResolveKnownCreationFactory(IInvocationOperation creation, SemanticModel? semanticModel)
    {
        return FactoryMethods.IsValueBearingCreation(creation.TargetMethod)
            ? ResolveFactorySymbol(creation, semanticModel)
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
                // factory by construction. A variable REASSIGNED before this use proves
                // nothing (`var host = f1; host = f2; host.CreateOptimistic(...)` holds f2,
                // not its initializer) -- unprovable keeps the diagnostic.
                var symbol = SymbolOf(receiver);
                if (symbol is null || IsReassignedBefore(symbol, receiver.Syntax, semanticModel))
                {
                    return null;
                }

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

    // The CONTEXT identity behind a factory symbol, resolved through its same-tree
    // `new MemoFactory(key)` initializer: unkeyed instances (null key) each own a fresh
    // context, keyed ones share one context per key. Resolved=false means unprovable (no
    // visible creation, or a non-constant key).
    public static (bool Resolved, object? ContextKey) ResolveFactoryContextKey(ISymbol factorySymbol, SemanticModel? semanticModel)
    {
        var initializer = InitializerOf(factorySymbol, semanticModel);
        while (initializer is IConversionOperation conversion)
        {
            initializer = conversion.Operand;
        }

        if (initializer is not IObjectCreationOperation creation
            || creation.Type is not INamedTypeSymbol { Name: "MemoFactory" } named
            || named.ContainingNamespace?.ToDisplayString() != "MemoizR")
        {
            return (false, null);
        }

        foreach (var argument in creation.Arguments)
        {
            if (argument.Parameter?.Name == "contextKey")
            {
                if (argument.Value.ConstantValue is not { HasValue: true } constant)
                {
                    return (false, null);
                }

                // The runtime constructor treats null/whitespace keys as UNKEYED
                // (string.IsNullOrWhiteSpace): each such factory owns a fresh context, so a
                // blank constant must normalize to the unkeyed case here or two
                // new MemoFactory("") instances would wrongly count as one shared context.
                var key = constant.Value is string text && string.IsNullOrWhiteSpace(text)
                    ? null
                    : constant.Value;
                return (true, key);
            }
        }

        return (true, null);
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

    // The shared resolver covers ordinary variable/property initializers AND deconstruction
    // designations (`var (state, _) = (f1.CreateOptimistic(...), 0)`), so provenance chases
    // the same shapes the delegate resolution does.
    private static IOperation? InitializerOf(ISymbol? variable, SemanticModel? semanticModel)
    {
        return ComputationLambdas.SameTreeInitializerOperation(variable, semanticModel);
    }
}
