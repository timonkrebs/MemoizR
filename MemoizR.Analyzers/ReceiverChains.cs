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
    public static ISymbol? ResolveFactorySymbol(IInvocationOperation invocation, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap = null)
    {
        return ResolveReceiverSymbol(ReceiverOf(invocation), semanticModel, depth: 0, argumentMap);
    }

    // A node reference (the signal a Set is invoked on) resolves through its same-tree
    // initializer to the creating invocation, then to that creation's factory. An INLINE
    // creation (`f.CreateSignal(0).Set(1)`) is its own provenance and resolves directly, and a
    // variable-to-variable ALIAS (`var state = s0;`) resolves through initializers until a
    // creation or a dead end -- the visited set breaks initializer cycles.
    public static ISymbol? ResolveCreatingFactorySymbol(IOperation? nodeReference, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap = null)
    {
        return ResolveCreatingFactorySymbol(nodeReference, semanticModel, argumentMap, visitedGetters: null);
    }

    private static ISymbol? ResolveCreatingFactorySymbol(IOperation? nodeReference, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap, HashSet<IMethodSymbol>? visitedGetters)
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
                    return ResolveKnownCreationFactory(creation, semanticModel, argumentMap);
                // A chased helper's PARAMETER hops to the call-site argument: `var a = s;
                // a.Set(...)` inside `Write(other)` is `other`'s provenance. Not when the
                // helper WROTE the parameter first, though -- a parameter binds the caller's
                // argument at entry, so any same-tree write that can run before this read is
                // a rebind (there is no missing initializer to stand in for), and the case
                // below resolves what was actually assigned instead.
                case IParameterReferenceOperation parameterReference
                    when argumentMap?.TryGetValue(parameterReference.Parameter, out var mapped) == true
                        && site is not null
                        && !ComputationLambdas.IsWrittenBefore(parameterReference.Parameter, site, semanticModel):
                    reference = mapped;
                    site = mapped.Syntax;
                    continue;
                case ILocalReferenceOperation or IFieldReferenceOperation or IParameterReferenceOperation or IPropertyReferenceOperation:
                    visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    var symbol = ComputationLambdas.ReferencedSymbol(reference);
                    if (symbol is null || !visited.Add(symbol) || site is null || IsReassignedBefore(symbol, site, semanticModel))
                    {
                        return null;
                    }

                    var initializer = ComputationLambdas.SameTreeInitializerOperation(symbol, semanticModel);
                    if (initializer is null)
                    {
                        // A get-only COMPUTED node property has no initializer: its getter's
                        // returns are the provenance instead.
                        return ResolveComputedGetterFactory(reference, symbol, semanticModel, argumentMap, visitedGetters);
                    }

                    site = initializer.Syntax;
                    reference = initializer;
                    continue;
                default:
                    return null;
            }
        }
    }

    // A get-only computed node property (`Signal<int> Other => f2.CreateSignal(1);`, indexers
    // included via the reference's argument map) resolves through its getter's returns --
    // accepted only when EVERY return agrees on the creating factory: a getter that can hand
    // back nodes from different factories stays unprovable, which keeps the diagnostic. The
    // visited set bounds mutually recursive getters.
    private static ISymbol? ResolveComputedGetterFactory(IOperation reference, ISymbol symbol, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap, HashSet<IMethodSymbol>? visitedGetters)
    {
        if (symbol is not IPropertySymbol { GetMethod: { } getter, SetMethod: null }
            || ComputationLambdas.ResolveMethodBody(getter, semanticModel) is not { } getterBody)
        {
            return null;
        }

        visitedGetters ??= new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        if (!visitedGetters.Add(getter))
        {
            return null;
        }

        var propertyMap = ComputationLambdas.BuildArgumentMap(reference, argumentMap);
        ISymbol? factory = null;
        foreach (var returned in ComputationLambdas.ReturnedValues(getterBody.Body))
        {
            var resolved = ResolveCreatingFactorySymbol(returned, semanticModel, propertyMap, visitedGetters);
            if (resolved is null || (factory is not null && !SymbolEqualityComparer.Default.Equals(factory, resolved)))
            {
                return null;
            }

            factory = resolved;
        }

        return factory;
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
        // provenance -- the same assignment is what InitializerOf resolves through). The
        // synthesis is READ-relative: a write that cannot run before this read, or a member
        // write that does not provably reach it, excuses nothing and falls to the scan below.
        var effectiveInitializer = ComputationLambdas.EffectiveInitializerAssignment(variable, semanticModel, reference);

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
    private static ISymbol? ResolveKnownCreationFactory(IInvocationOperation creation, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        return FactoryMethods.IsValueBearingCreation(creation.TargetMethod)
            ? ResolveFactorySymbol(creation, semanticModel, argumentMap)
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

    private static ISymbol? ResolveReceiverSymbol(IOperation? receiver, SemanticModel? semanticModel, int depth, Dictionary<IParameterSymbol, IOperation>? argumentMap = null)
    {
        if (depth > 8 || receiver is null)
        {
            return null;
        }

        switch (receiver)
        {
            case IInvocationOperation chained:
                return ResolveReceiverSymbol(ReceiverOf(chained), semanticModel, depth + 1, argumentMap);
            case IConversionOperation conversion:
                return ResolveReceiverSymbol(conversion.Operand, semanticModel, depth + 1, argumentMap);

            // A chased helper's FACTORY parameter hops to the call-site argument, like the
            // node-reference hop above: `f.CreateSignal(0)` inside `Write(f2)` is created
            // by f2. Same rebind guard: a parameter overwritten before this use resolves
            // through the effective-initializer machinery below instead.
            case IParameterReferenceOperation parameterReference
                when argumentMap?.TryGetValue(parameterReference.Parameter, out var mapped) == true
                    && !ComputationLambdas.IsWrittenBefore(parameterReference.Parameter, receiver.Syntax, semanticModel):
                return ResolveReceiverSymbol(mapped, semanticModel, depth + 1, argumentMap);
            case ILocalReferenceOperation or IFieldReferenceOperation or IParameterReferenceOperation or IPropertyReferenceOperation:
                // An intermediate (a stored ReactionBuilder, a factory alias) resolves through
                // its initializer; when that gives out, the reference symbol itself is the
                // identity -- two creations hanging off the same local/field/parameter share a
                // factory by construction. A variable REASSIGNED before this use proves
                // nothing (`var host = f1; host = f2; host.CreateOptimistic(...)` holds f2,
                // not its initializer) -- unprovable keeps the diagnostic.
                var symbol = ComputationLambdas.ReferencedSymbol(receiver);
                if (symbol is null || IsReassignedBefore(symbol, receiver.Syntax, semanticModel))
                {
                    return null;
                }

                var initialized = ComputationLambdas.SameTreeInitializerOperation(symbol, semanticModel);
                if (initialized is not null && ResolveReceiverSymbol(initialized, semanticModel, depth + 1, argumentMap) is { } through)
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
        var declared = ComputationLambdas.SameTreeInitializerOperation(factorySymbol, semanticModel);

        if (declared is null || ComputationLambdas.Unwrap(declared) is not IObjectCreationOperation creation
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

}
