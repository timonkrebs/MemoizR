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
            // Keyed per CALL SITE and ARGUMENT BINDING, not per method: the same helper (or
            // the same nested call inside it) reached with different signal arguments has
            // different target provenance each time, while a recursive helper -- whose
            // rebuilt map carries the same substituted values -- stops.
            var visitedCalls = new HashSet<(SyntaxNode, IMethodSymbol, string)>();
            InspectExecutedBody(context, invocation, computation.Body, actorHost, visitedCalls, argumentMap: null);
        }

        // Patch shapes resolved beyond plain arguments -- assembled by an out-helper, or
        // returned by a computed delegate property -- still run under the evaluation lock,
        // so their Sets throw exactly like an inline patch's.
        if (FactoryMethods.IsOptimisticPatchHost(invocation.TargetMethod))
        {
            foreach (var argument in invocation.Arguments)
            {
                if (argument.Parameter?.Type is not { TypeKind: TypeKind.Delegate })
                {
                    continue;
                }

                foreach (var (body, map) in ComputationLambdas.AssembledPatchBodies(argument.Value, invocation.SemanticModel))
                {
                    var visitedCalls = new HashSet<(SyntaxNode, IMethodSymbol, string)>();
                    InspectExecutedBody(context, invocation, body.Body, actorHost, visitedCalls, map);
                }
            }
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
        HashSet<(SyntaxNode, IMethodSymbol, string)> visitedCalls,
        Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        foreach (var operation in ComputationLambdas.DescendDirectExecution(body))
        {
            if (operation is IInvocationOperation inner && IsSameEngineSet(inner.TargetMethod, actorHost))
            {
                if (!IsProvablyCrossFactory(host, inner, argumentMap, actorHost))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.SetInsideComputation,
                        ComputationLambdas.NameLocation(inner),
                        SendableSymbolClassifier.Display(inner.TargetMethod.ContainingType),
                        inner.TargetMethod.Name));
                }

                continue;
            }

            if (operation is IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke, Instance: { } callee })
            {
                InspectInvokedDelegate(context, host, callee, actorHost, visitedCalls, argumentMap);
                continue;
            }

            InspectExecutedCalls(context, host, operation, actorHost, visitedCalls, argumentMap);
        }
    }

    // A delegate the computation synchronously INVOKES executes its body under the same
    // evaluation lock: `d()` with a same-tree `Func<int> d = () => { _ = v.Set(1); ... };`
    // throws exactly like the inline Set -- while a merely BUILT callback stays pruned
    // (deferred execution holds no lock, and building one is this rule's own fix guidance).
    // Aliases and parameter bindings resolve like the executed-call chase; anything
    // unresolvable keeps the runtime backstop.
    private static void InspectInvokedDelegate(
        OperationAnalysisContext context,
        IInvocationOperation host,
        IOperation callee,
        bool actorHost,
        HashSet<(SyntaxNode, IMethodSymbol, string)> visitedCalls,
        Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        var resolved = ComputationLambdas.ResolveDelegateValue(ComputationLambdas.ResolveConditionalReceiver(callee), host.SemanticModel, argumentMap);
        var found = false;
        foreach (var body in ComputationLambdas.OfArgumentValue(resolved, host.SemanticModel))
        {
            found = true;
            if (visitedCalls.Add((body.Scope, host.TargetMethod, ComputationLambdas.ArgumentMapKey(argumentMap))))
            {
                InspectExecutedBody(context, host, body.Body, actorHost, visitedCalls, argumentMap);
            }
        }

        if (!found)
        {
            InspectInvokedFactoryResult(context, host, resolved, actorHost, visitedCalls, argumentMap);
        }
    }

    // `Get()(x)` executes whatever the same-tree factory returned -- and `Step()` whatever
    // the computed property's getter returned -- immediately and under the same lock: the
    // returns are walked with their own maps, cycles bounded by the per-body visited guard.
    private static void InspectInvokedFactoryResult(
        OperationAnalysisContext context,
        IInvocationOperation host,
        IOperation resolved,
        bool actorHost,
        HashSet<(SyntaxNode, IMethodSymbol, string)> visitedCalls,
        Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        while (resolved is IConversionOperation conversion)
        {
            resolved = conversion.Operand;
        }

        if (resolved is IInvocationOperation call
            && ComputationLambdas.ResolveMethodBody(call.TargetMethod, host.SemanticModel) is { } factory)
        {
            InspectReturnedInvokedBodies(context, host, factory, ComputationLambdas.BuildArgumentMap(call, argumentMap), actorHost, visitedCalls);
            return;
        }

        if (ComputationLambdas.ReferencedVariable(resolved) is IPropertySymbol { GetMethod: { } getter, SetMethod: null }
            && ComputationLambdas.ResolveMethodBody(getter, host.SemanticModel) is { } getterBody)
        {
            InspectReturnedInvokedBodies(context, host, getterBody, ComputationLambdas.BuildArgumentMap(resolved, argumentMap), actorHost, visitedCalls);
        }
    }

    private static void InspectReturnedInvokedBodies(
        OperationAnalysisContext context,
        IInvocationOperation host,
        ComputationLambdas.ComputationBody source,
        Dictionary<IParameterSymbol, IOperation>? sourceMap,
        bool actorHost,
        HashSet<(SyntaxNode, IMethodSymbol, string)> visitedCalls)
    {
        foreach (var (body, map) in ComputationLambdas.ReturnedBodies(source, host.SemanticModel, sourceMap))
        {
            if (visitedCalls.Add((body.Scope, host.TargetMethod, ComputationLambdas.ArgumentMapKey(map))))
            {
                InspectExecutedBody(context, host, body.Body, actorHost, visitedCalls, map);
            }
        }
    }

    private static void InspectExecutedCalls(
        OperationAnalysisContext context,
        IInvocationOperation host,
        IOperation operation,
        bool actorHost,
        HashSet<(SyntaxNode, IMethodSymbol, string)> visitedCalls,
        Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        foreach (var method in ComputationLambdas.ExecutedMethods(operation))
        {
            if (ComputationLambdas.IsInsideNameOf(operation)
                || ComputationLambdas.ResolveMethodBody(method, host.SemanticModel) is not { } helper)
            {
                continue;
            }

            var nestedMap = ComputationLambdas.BuildArgumentMap(operation, argumentMap);
            if (visitedCalls.Add((operation.Syntax, method, ComputationLambdas.ArgumentMapKey(nestedMap))))
            {
                InspectExecutedBody(context, host, helper.Body, actorHost, visitedCalls, nestedMap);
            }
        }
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
    private static bool IsProvablyCrossFactory(IInvocationOperation host, IInvocationOperation setInvocation, Dictionary<IParameterSymbol, IOperation>? argumentMap, bool actorHost)
    {
        if (actorHost)
        {
            return false;
        }

        var hostFactory = ResolveHostFactory(host);
        var targetFactory = ReceiverChains.ResolveCreatingFactorySymbol(setInvocation.Instance, setInvocation.SemanticModel, argumentMap);
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
            // Computed state properties (indexers included) resolve inside the node
            // resolver, through their getter's agreeing returns.
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
