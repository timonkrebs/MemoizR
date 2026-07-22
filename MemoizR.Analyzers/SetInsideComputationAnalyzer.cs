using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// MZR003: Signal.Set / EagerRelativeSignal.Set inside a computation whose flow holds the
// evaluation lock in upgradeable mode is an exclusive-inside-upgradeable acquisition, which the
// AsyncAsymmetricLock deliberately converts into an InvalidOperationException (a write inside a
// read of the same graph is a feedback loop, and waiting would deadlock). This rule surfaces
// that runtime exception at build time. Hosts whose children run on forced fresh scopes
// (ConcurrentMap, ConcurrentRace) are excluded -- see FactoryMethods.IsSameFlowEvaluationHost.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SetInsideComputationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.SetInsideComputation);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!FactoryMethods.IsSameFlowEvaluationHost(invocation.TargetMethod))
        {
            return;
        }

        var actorHost = FactoryMethods.IsActorEngineHost(invocation.TargetMethod);

        foreach (var computation in ComputationLambdas.OfInvocation(invocation))
        {
            var visitedHelpers = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            InspectExecutedBody(context, invocation, computation.Body, actorHost, visitedHelpers, argumentMap: null);
        }
    }

    // Direct execution path only: a Set inside a callback the computation merely BUILDS (the
    // diagnostic's suggested escape) runs later, off the evaluation's flow -- but a same-tree
    // helper the computation CALLS executes its Set under the same evaluation lock and throws
    // identically, so called bodies are chased (the visited set bounds call cycles; metadata
    // bodies stay unwalkable, and the runtime exception still covers those).
    private static void InspectExecutedBody(
        OperationAnalysisContext context,
        IInvocationOperation host,
        IOperation body,
        bool actorHost,
        HashSet<IMethodSymbol> visitedHelpers,
        Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        foreach (var operation in ComputationLambdas.DescendDirectExecution(body))
        {
            if (operation is IInvocationOperation inner && IsSameEngineSet(inner.TargetMethod, actorHost))
            {
                if (!IsProvablyCrossFactory(host, inner, SubstituteArguments(inner.Instance, argumentMap), actorHost))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.SetInsideComputation,
                        ComputationLambdas.NameLocation(inner),
                        SendableSymbolClassifier.Display(inner.TargetMethod.ContainingType),
                        inner.TargetMethod.Name));
                }

                continue;
            }

            foreach (var method in ComputationLambdas.ExecutedMethods(operation))
            {
                if (!ComputationLambdas.IsInsideNameOf(operation)
                    && visitedHelpers.Add(method)
                    && ComputationLambdas.ResolveMethodBody(method, host.SemanticModel) is { } helper)
                {
                    InspectExecutedBody(context, host, helper.Body, actorHost, visitedHelpers, BuildArgumentMap(operation, argumentMap));
                }
            }
        }
    }

    // Call-site arguments substitute for the helper's parameters when judging a chased Set's
    // target provenance: in `void Write(Signal<int> s) => s.Set(2);` called as `Write(other)`,
    // the target is `other`, exactly as the inline form -- a bare parameter would resolve to
    // nothing and lose a legitimate cross-factory suppression. Maps are built pre-substituted,
    // so nested helper calls resolve through to the original computation's operations. (One
    // map per helper -- the visited set keeps the FIRST call site's arguments; re-walking per
    // call site is not worth the cost for the precision it would buy.)
    private static Dictionary<IParameterSymbol, IOperation>? BuildArgumentMap(IOperation operation, Dictionary<IParameterSymbol, IOperation>? outer)
    {
        var arguments = operation switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => default,
        };

        if (arguments.IsDefaultOrEmpty)
        {
            return null;
        }

        Dictionary<IParameterSymbol, IOperation>? map = null;
        foreach (var argument in arguments)
        {
            if (argument.Parameter is { } parameter && SubstituteArguments(argument.Value, outer) is { } value)
            {
                map ??= new Dictionary<IParameterSymbol, IOperation>(SymbolEqualityComparer.Default);
                map[parameter] = value;
            }
        }

        return map;
    }

    private static IOperation? SubstituteArguments(IOperation? reference, Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        var current = reference;
        while (current is IConversionOperation conversion)
        {
            current = conversion.Operand;
        }

        return current is IParameterReferenceOperation parameterReference
            && argumentMap?.TryGetValue(parameterReference.Parameter, out var argument) == true
            ? argument
            : reference;
    }

    // A cross-CONTEXT lock-engine Set does not throw at runtime: the Set locks the target
    // signal's own context, where the computing graph holds nothing. Skipped only when the
    // host's and the Set target's factories resolve to different symbols AND their CONTEXTS
    // provably differ -- two factory variables constructed with the same key share one context
    // (and its lock), so mere variable inequality proves nothing there. Anything unprovable (a
    // field signal wired in a constructor, a parameter, a non-constant key) keeps the
    // diagnostic, because the overwhelmingly common case is one shared context and the runtime
    // exception is deterministic there. Actor hosts are exempt from the check: ActorSignal.Set
    // rejects on the flow's frame, which any actor computation carries regardless of context.
    private static bool IsProvablyCrossFactory(IInvocationOperation host, IInvocationOperation setInvocation, IOperation? targetReference, bool actorHost)
    {
        if (actorHost)
        {
            return false;
        }

        var hostFactory = ResolveHostFactory(host);
        var targetFactory = ReceiverChains.ResolveCreatingFactorySymbol(targetReference ?? setInvocation.Instance, setInvocation.SemanticModel);
        if (hostFactory is null
            || targetFactory is null
            || SymbolEqualityComparer.Default.Equals(hostFactory, targetFactory))
        {
            return false;
        }

        var hostContext = ReceiverChains.ResolveFactoryContextKey(hostFactory, host.SemanticModel);
        var targetContext = ReceiverChains.ResolveFactoryContextKey(targetFactory, setInvocation.SemanticModel);
        if (!hostContext.Resolved || !targetContext.Resolved)
        {
            return false;
        }

        // An unkeyed instance owns a fresh context, so any pairing involving one is disjoint;
        // two keyed instances share exactly when their constant keys match.
        return hostContext.ContextKey is null
            || targetContext.ContextKey is null
            || !Equals(hostContext.ContextKey, targetContext.ContextKey);
    }

    // The factory whose context the HOST's evaluation flow locks. For an optimistic patch the
    // Apply receiver is the action context, which resolves to nothing -- the patch runs inside
    // the STATE's view computation, so its host factory is whatever created the OptimisticState
    // argument (resolved like any node reference, through same-tree initializers).
    private static ISymbol? ResolveHostFactory(IInvocationOperation host)
    {
        if (FactoryMethods.IsOptimisticPatchHost(host.TargetMethod))
        {
            var stateArgument = host.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value;
            return ReceiverChains.ResolveCreatingFactorySymbol(stateArgument, host.SemanticModel);
        }

        return ReceiverChains.ResolveFactorySymbol(host, host.SemanticModel);
    }

    // A write API that throws in THIS host's engine: ActorSignal.Set inside an actor
    // computation, or lock-engine Signal/EagerRelativeSignal.Set and MemoBase.Invalidate (the
    // ADR 0007 refresh, which takes the same exclusive lock as Set) inside a lock-engine
    // computation. A cross-engine write takes no same-flow lock and does not throw, so it must
    // not be flagged.
    private static bool IsSameEngineSet(IMethodSymbol method, bool actorHost)
    {
        var type = method.ContainingType?.OriginalDefinition;
        if (type is not { Arity: 1 } || type.ContainingNamespace?.ToDisplayString() != "MemoizR"
            || !FactoryMethods.IsLibraryType(type))
        {
            // A source-shadowed MemoizR.Signal<T> lookalike's Set takes no evaluation lock and
            // does not throw -- name matching alone must not claim it does (same identity rule
            // as the factory-host classification).
            return false;
        }

        if (actorHost)
        {
            return method.Name == "Set" && type.Name == "ActorSignal";
        }

        return (method.Name == "Set" && type.Name is "Signal" or "EagerRelativeSignal")
            || (method.Name == "Invalidate" && type.Name == "MemoBase");
    }
}
