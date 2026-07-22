using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// MZR004, the closure-capture mirror of MZR001 (ADR 0007 phase 5): an optimistic patch is
// stored in the overlay and re-executed by the view's computation on whichever flow pulls the
// optimistic state, so everything its closure captures crosses flows exactly like a node value.
// A capture whose type is not Sendable (a List<int> local), or a read of writable state on a
// non-Sendable enclosing object (a non-readonly field, a settable or ref-returning property),
// is therefore unsynchronized cross-flow sharing -- and so are a method-group patch's receiver
// (including a mutable struct boxed into the delegate), a bare `this` handed to a helper,
// static state the patch reads directly or through same-tree helpers (shared without any
// capture at all), closure state of outside-the-patch local functions it calls, and an
// already-built delegate that resolves to nothing walkable. Reads of Sendable-typed captures
// stay unflagged -- capturing the action payload or other immutable snapshots is the idiomatic
// pattern. This rule exists because the
// RUNTIME cannot check it: closure display classes always carry writable fields, so a
// structural runtime check would reject every capturing lambda. Captured-state WRITES inside a
// patch are MZR002's territory (Apply is a computation host), and a Set inside one is MZR003's.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OptimisticPatchCaptureAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.NonSendablePatchCapture);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // The classifier caches verdicts per type symbol, so it must be scoped to one
        // compilation: symbols cached across compilations would be both wrong and a leak.
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
        if (!FactoryMethods.IsOptimisticPatchHost(invocation.TargetMethod))
        {
            return;
        }

        // One report per captured symbol: a capture is a property of the closure, not of each
        // of its reads.
        var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var argument in invocation.Arguments)
        {
            InspectPatchArgument(context, classifier, argument.Value, invocation.SemanticModel, reported);
        }
    }

    // One patch argument, all shapes: a lambda (or a delegate variable resolving to one) walks
    // its body; a method group (however stored) checks its receiver and walks its same-tree
    // body; a delegate-typed argument resolving to NEITHER is an already-built closure Roslyn
    // cannot see into -- flagged, because the overlay stores it all the same, this rule is the
    // only check the patch will ever get, and a delegate can capture arbitrary mutable state
    // (the classifier rejects delegate-typed values for the same reason).
    private static void InspectPatchArgument(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation value,
        SemanticModel? semanticModel,
        HashSet<ISymbol> reported)
    {
        // A delegate variable that is REASSIGNED after its initializer no longer proves which
        // closure the overlay stores -- the initializer may be harmless while the reassignment
        // captures anything -- so it is treated like an unresolvable delegate instead of
        // trusted. (MZR003 deliberately keeps trusting initializers: its runtime exception
        // still covers reassignment, this rule's does not exist.)
        if (value.Type is { TypeKind: TypeKind.Delegate }
            && FindReassignedLink(value, semanticModel) is { } reassigned)
        {
            Report(
                context,
                value,
                reassigned,
                reassigned.Name,
                "it is reassigned after its initializer, so the delegate stored in the overlay cannot be resolved from this call site, and a delegate can capture arbitrary mutable state",
                reported);
            return;
        }

        var methodReference = ResolveMethodReference(value, semanticModel, visitedVariables: null);
        if (methodReference is not null)
        {
            InspectMethodGroupReceiver(context, classifier, methodReference, reported);
        }

        var bodyResolved = false;
        foreach (var patch in ComputationLambdas.OfArgumentValue(value, semanticModel))
        {
            bodyResolved = true;
            InspectPatchBody(context, classifier, patch, semanticModel, reported);
        }

        // A SOURCE-declared method group whose body lives in another file cannot be walked:
        // nothing checks the statics it executes, so unverifiable means flagged -- while
        // metadata targets (Math.Abs) stay trusted like every other external contract.
        if (methodReference is not null && !bodyResolved
            && methodReference.Method.DeclaringSyntaxReferences.Length > 0)
        {
            Report(
                context,
                value,
                methodReference.Method,
                methodReference.Method.Name,
                "its body is declared in another file, so what the stored patch executes cannot be verified from this call site (define the patch in this file, or wrap verified state in a lambda)",
                reported);
        }

        if (methodReference is null && !bodyResolved && value.Type is { TypeKind: TypeKind.Delegate })
        {
            var symbol = ReceiverSymbol(Unwrap(value));
            Report(
                context,
                value,
                symbol ?? value.Type,
                symbol?.Name ?? SendableSymbolClassifier.Display(value.Type),
                "it is an already-built delegate whose closure cannot be resolved from this call site, and a delegate can capture arbitrary mutable state (define the patch inline or as a same-tree method)",
                reported);
        }
    }

    // A method-group patch (`ctx.Apply(state, helper.Patch)`) captures its RECEIVER into the
    // stored delegate -- shared across pull flows even when the method body lives in metadata
    // or another file and cannot be walked.
    private static void InspectMethodGroupReceiver(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IMethodReferenceOperation methodReference,
        HashSet<ISymbol> reported)
    {
        if (methodReference.Instance is not { } receiver || receiver.Type is not { } receiverType)
        {
            return;
        }

        var symbol = ReceiverSymbol(receiver);
        var name = receiver is IInstanceReferenceOperation ? "this" : symbol?.Name ?? SendableSymbolClassifier.Display(receiverType);
        var reason = classifier.GetNotSendableReason(receiverType);
        if (reason is null)
        {
            // A Sendable verdict for a VALUE TYPE rests on copy semantics -- but a method-group
            // delegate stores ONE boxed receiver that every re-execution shares, and a
            // non-readonly struct method mutates that box in place. (A readonly method cannot,
            // and the box is reachable only through the delegate. Extension methods need no
            // exemption: CS1113 forbids creating a delegate from a value-type extension
            // receiver, so only real instance methods reach this branch.)
            if (receiverType.IsValueType && !methodReference.Method.IsReadOnly)
            {
                Report(
                    context,
                    receiver,
                    symbol ?? receiverType,
                    name,
                    $"its non-readonly method '{methodReference.Method.Name}' can mutate the boxed receiver the stored delegate shares across flows",
                    reported);
            }

            return;
        }

        Report(
            context,
            receiver,
            symbol ?? receiverType,
            name,
            $"its type '{SendableSymbolClassifier.Display(receiverType)}' is not Sendable ({reason})",
            reported);
    }

    // The delegate argument may be the method group itself, a conversion over it, or a variable
    // whose (same-tree) initializer holds it -- the same resolution ComputationLambdas applies
    // to computation bodies, so a `Func<int,int> patch = helper.Patch;` stored one statement
    // earlier does not hide the receiver. GetOperation on an initializer yields the reference
    // without the delegate-creation wrapper, hence the bare case.
    private static IMethodReferenceOperation? ResolveMethodReference(
        IOperation? value,
        SemanticModel? semanticModel,
        HashSet<ISymbol>? visitedVariables)
    {
        while (true)
        {
            switch (value)
            {
                case IConversionOperation conversion:
                    value = conversion.Operand;
                    continue;
                case IDelegateCreationOperation creation:
                    value = creation.Target;
                    continue;
                case IMethodReferenceOperation methodReference:
                    return methodReference;
                case ILocalReferenceOperation or IFieldReferenceOperation or IPropertyReferenceOperation:
                    visitedVariables ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    var variable = ComputationLambdas.ReferencedVariable(value);
                    if (variable is null || !visitedVariables.Add(variable))
                    {
                        return null;
                    }

                    value = ComputationLambdas.SameTreeInitializerOperation(variable, semanticModel);
                    continue;
                default:
                    return null;
            }
        }
    }

    // Any assignment beyond the declaration initializer (including a ref/out handoff, or a
    // write through a ref alias) that can EXECUTE before this READ: a same-tree syntactic
    // scan, so a reassignment in another file is residual risk like every same-tree-only
    // resolution here. A later straight-line assignment cannot change the delegate this call
    // already stored -- flagging it would punish the common rebind-after-use.
    private static bool IsReassignedBefore(ISymbol variable, IOperation readReference, SemanticModel? semanticModel)
    {
        if (semanticModel is null)
        {
            return false;
        }

        // The sole assignment standing in for a missing initializer is initialization, not a
        // rebind (`Func<int,int> patch; patch = static x => x;` is the declaration in two
        // steps) -- but only when it can actually RUN before this read: a future write proves
        // nothing about the value read here (a constructor- or externally-supplied delegate
        // would slip through on its strength).
        var effectiveInitializer = ComputationLambdas.EffectiveInitializerAssignment(variable, semanticModel);
        if (effectiveInitializer is not null
            && !ComputationLambdas.CanExecuteBefore(effectiveInitializer, readReference.Syntax, variable, semanticModel))
        {
            return true;
        }

        foreach (var node in semanticModel.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (!ReferenceEquals(node, effectiveInitializer)
                && ComputationLambdas.ReassignmentTargets(node) is { } targets
                && targets.Any(target => ComputationLambdas.WritesVariable(target, variable, semanticModel)
                    && WritesReadInstance(target, variable, readReference, semanticModel))
                && ComputationLambdas.CanExecuteBefore(node, readReference.Syntax, variable, semanticModel))
            {
                return true;
            }
        }

        return false;
    }

    // A write through a local that provably holds a DIFFERENT instance (initialized with its
    // own `new`) cannot rebind the member being read through some other receiver:
    // `other.patch = evil;` says nothing about `this.patch`. Approximation: the receiver's
    // fresh initializer is the proof; a receiver later re-aliased to the read instance is
    // accepted residual risk.
    private static bool WritesReadInstance(ExpressionSyntax target, ISymbol variable, IOperation readReference, SemanticModel semanticModel)
    {
        if (variable is not (IFieldSymbol or IPropertySymbol)
            || target is not MemberAccessExpressionSyntax { Expression: { } receiverExpression })
        {
            return true;
        }

        if (semanticModel.GetSymbolInfo(receiverExpression).Symbol is not ILocalSymbol receiver
            || !IsFreshInstance(receiver)
            || IsReceiverOf(readReference, receiver))
        {
            return true;
        }

        return false;
    }

    private static bool IsFreshInstance(ILocalSymbol local)
    {
        return local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
            is VariableDeclaratorSyntax { Initializer.Value: BaseObjectCreationExpressionSyntax };
    }

    private static bool IsReceiverOf(IOperation readReference, ILocalSymbol receiver)
    {
        var instance = Unwrap(readReference) switch
        {
            IFieldReferenceOperation field => field.Instance,
            IPropertyReferenceOperation property => property.Instance,
            _ => null,
        };

        return instance is not null
            && SymbolEqualityComparer.Default.Equals(ComputationLambdas.ReferencedVariable(Unwrap(instance)), receiver);
    }

    // The reassignment check applies to EVERY variable in the alias chain, each against the
    // site where its value is READ: `p0 = evil; Func<int,int> patch = p0;` stores p0's
    // post-reassignment closure even though patch itself is never written again. A link whose
    // initializer is the computation itself (a lambda or method group) ends the chain.
    private static ISymbol? FindReassignedLink(IOperation value, SemanticModel? semanticModel)
    {
        var reference = Unwrap(value);
        HashSet<ISymbol>? visited = null;
        while (ReceiverSymbol(reference) is (ILocalSymbol or IFieldSymbol or IPropertySymbol) and { } variable)
        {
            if (IsReassignedBefore(variable, reference, semanticModel))
            {
                return variable;
            }

            visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (!visited.Add(variable)
                || ComputationLambdas.SameTreeInitializerOperation(variable, semanticModel) is not { } initializer)
            {
                return null;
            }

            reference = Unwrap(initializer);
        }

        return null;
    }

    private static IOperation Unwrap(IOperation value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value;
    }

    private static ISymbol? ReceiverSymbol(IOperation receiver)
    {
        return receiver switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };
    }

    private static void InspectCapture(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        SyntaxNode scope,
        SyntaxNode patchScope,
        SemanticModel? semanticModel,
        HashSet<ISymbol> reported,
        HashSet<IMethodSymbol>? visitedGetters = null)
    {
        switch (operation)
        {
            case ILocalReferenceOperation local when IsSharedCapture(local.Local, scope, patchScope):
                ReportCapturedVariable(context, classifier, operation, local.Local, local.Local.Type, reported);
                break;
            case IParameterReferenceOperation parameter when IsSharedCapture(parameter.Parameter, scope, patchScope):
                ReportCapturedVariable(context, classifier, operation, parameter.Parameter, parameter.Parameter.Type, reported);
                break;

            // A member access on the enclosing object captures `this`. The per-member verdicts
            // REFINE a non-Sendable enclosing object -- they say which reads make sharing it
            // dangerous: writable storage is flagged outright (the patch re-reads it on other
            // flows while the owner mutates it freely); readonly/get-only members are held to
            // the member TYPE's sendability (the object handed out is what gets shared). An
            // enclosing type the classifier accepts ([Sendable], structurally immutable) was
            // vetted WHOLESALE -- re-walking its members would override that trust, exactly
            // where the runtime SendableChecker trusts it. A COMPUTED get-only property is a
            // helper call in disguise: its same-tree getter body is walked with these same
            // verdicts (`int Counter => counter;` re-reads the mutable field on every replay).
            case IFieldReferenceOperation { Field.IsStatic: false } field when IsOnNonSendableEnclosingInstance(field.Instance, classifier):
                InspectStateRead(
                    context, classifier, operation, field.Field, field.Field.Type,
                    writable: !field.Field.IsReadOnly, "it is writable state of the enclosing object", reported);
                break;
            case IPropertyReferenceOperation { Property.IsStatic: false } property when IsOnNonSendableEnclosingInstance(property.Instance, classifier):
                if (IsSettable(property.Property))
                {
                    Report(context, operation, property.Property, property.Property.Name, "it is writable state of the enclosing object", reported);
                }
                else if (HasBackingStorage(property.Property))
                {
                    ReportIfNotSendable(context, classifier, operation, property.Property, property.Property.Type, reported);
                }
                else
                {
                    InspectComputedGetter(context, classifier, property.Property, patchScope, semanticModel, reported, visitedGetters);
                }

                break;

            // A bare `this` -- the implicit receiver of a helper call (`ReadCounter()`) or an
            // explicit argument (`Use(this)`) -- captures the whole enclosing object without
            // naming a member, so it is held to the TYPE's sendability like a method-group
            // receiver: hiding a state read behind a helper must not evade what the direct read
            // would flag. Receivers of the member reads the cases above handle are skipped;
            // those get the finer per-member verdict (or the same wholesale trust).
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } instance
                when instance.Type is { } enclosingType && !IsInspectedMemberReceiver(instance):
                var reason = classifier.GetNotSendableReason(enclosingType);
                if (reason is not null)
                {
                    Report(
                        context,
                        operation,
                        enclosingType,
                        "this",
                        $"its type '{SendableSymbolClassifier.Display(enclosingType)}' is not Sendable ({reason})",
                        reported);
                }

                break;
        }
    }

    // A computed get-only property on a non-Sendable enclosing type is a helper call in
    // disguise: its getter body re-executes on every replay, so it is walked with the same
    // member verdicts against the getter's own scope. The visited set bounds mutually
    // recursive getters; metadata and auto-properties never reach here (HasBackingStorage
    // keeps them on the type verdict).
    private static void InspectComputedGetter(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IPropertySymbol property,
        SyntaxNode patchScope,
        SemanticModel? semanticModel,
        HashSet<ISymbol> reported,
        HashSet<IMethodSymbol>? visitedGetters)
    {
        if (property.GetMethod is not { } getter
            || ComputationLambdas.ResolveMethodBody(getter, semanticModel) is not { } body)
        {
            return;
        }

        visitedGetters ??= new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        if (!visitedGetters.Add(getter))
        {
            return;
        }

        // Direct execution only: the getter body is EXECUTED code, not stored closure -- a
        // callback it builds and discards is allocated fresh per replay and never runs, so
        // its captures must not count (unlike the patch's own nested callbacks, which the
        // stored display chain pins).
        foreach (var inner in ComputationLambdas.DescendDirectExecution(body.Body))
        {
            InspectCapture(context, classifier, inner, body.Scope, patchScope, semanticModel, reported, visitedGetters);
        }
    }

    // Static state needs no capture to be shared: the read is baked into the stored delegate
    // and re-executes on pull flows while any code mutates the static. Same verdicts as
    // enclosing-object state; const fields are compile-time values, not storage. Separate from
    // InspectCapture because the helper walk below applies it to CALLED method bodies too.
    private static void InspectStaticRead(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        HashSet<ISymbol> reported)
    {
        switch (operation)
        {
            case IFieldReferenceOperation { Field: { IsStatic: true, IsConst: false } } staticField:
                InspectStateRead(
                    context, classifier, operation, staticField.Field, staticField.Field.Type,
                    writable: !staticField.Field.IsReadOnly, "it is writable static state", reported);
                break;
            case IPropertyReferenceOperation { Property.IsStatic: true } staticProperty:
                InspectStaticProperty(context, classifier, operation, staticProperty.Property, reported);
                break;

            // An event's backing delegate is writable storage by construction: any subscriber
            // mutates it, so a static event read/invoke shares mutable state like a writable
            // static field.
            case IEventReferenceOperation { Event.IsStatic: true } staticEvent:
                Report(
                    context, operation, staticEvent.Event, staticEvent.Event.Name,
                    "it is writable static state (subscribing mutates the event's backing delegate)", reported);
                break;
        }
    }

    private static void InspectStaticProperty(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        IPropertySymbol property,
        HashSet<ISymbol> reported)
    {
        if (IsSettable(property))
        {
            Report(context, operation, property, property.Name, "it is writable static state", reported);
            return;
        }

        // Only BACKING STORAGE holds a shared object: a computed getter may hand out a fresh
        // value on every replay (`static List<int> Items => new();`), and what it actually
        // returns is covered by the executed chase walking its body. Auto-properties and
        // metadata properties (no walkable body) keep the type verdict.
        if (HasBackingStorage(property))
        {
            ReportIfNotSendable(context, classifier, operation, property, property.Type, reported);
        }
    }

    private static bool HasBackingStorage(IPropertySymbol property)
    {
        return property.GetMethod?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() switch
        {
            AccessorDeclarationSyntax { Body: null, ExpressionBody: null } => true, // auto-property
            null => true,                                                          // metadata: unwalkable
            _ => false,                                                            // computed: chased instead
        };
    }

    // Two walks with different reach. Closure CONTENTS (captures, receivers, `this`) are
    // storage facts -- a callback the patch merely builds still pins them in the stored
    // display chain -- so they use the FULL walk, chasing outside local functions wherever
    // they are referenced. Static READS happen only on execution: one inside a built callback
    // runs later, off the overlay's re-execution, on whatever flow invokes it -- so statics
    // (and the helper chase that finds them) follow MZR003's direct-execution pruning.
    private static void InspectPatchBody(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        ComputationLambdas.ComputationBody patch,
        SemanticModel? semanticModel,
        HashSet<ISymbol> reported)
    {
        var visitedLocalFunctions = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var operation in ComputationLambdas.Descend(patch.Body))
        {
            InspectCapture(context, classifier, operation, patch.Scope, patch.Scope, semanticModel, reported);
            InspectCapturedLocalFunction(context, classifier, operation, patch.Scope, semanticModel, visitedLocalFunctions, reported);
        }

        // Both chases are bounded per SITE, not per symbol: the same delegate reassigned and
        // invoked again executes a different closure, and the same helper called with
        // different delegate arguments executes different bodies -- while recursion re-reaches
        // the same site and stops.
        var visitedCalls = new HashSet<(SyntaxNode, IMethodSymbol)>();
        var visitedInvokes = new HashSet<SyntaxNode>();
        foreach (var operation in ComputationLambdas.DescendDirectExecution(patch.Body))
        {
            InspectStaticRead(context, classifier, operation, reported);
            InspectExecutedHelper(context, classifier, operation, semanticModel, visitedCalls, visitedInvokes, argumentMap: null, reported);
        }
    }

    // A LOCAL FUNCTION referenced by the patch (called, or lifted into a delegate) has no
    // receiver: its closure is the patch's own environment, so its body is inspected for
    // captures like patch code -- against its own declaration scope, which keeps helper-call
    // locals out. One declared inside the patch is already covered by the surrounding walk.
    private static void InspectCapturedLocalFunction(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        SyntaxNode patchScope,
        SemanticModel? semanticModel,
        HashSet<IMethodSymbol> visited,
        HashSet<ISymbol> reported)
    {
        var method = ReferencedMethod(operation);
        if (method is not { MethodKind: MethodKind.LocalFunction }
            || ComputationLambdas.IsInsideNameOf(operation)
            || !ComputationLambdas.IsDeclaredOutside(method, patchScope)
            || !visited.Add(method)
            || ComputationLambdas.ResolveMethodBody(method, semanticModel) is not { } helper)
        {
            return;
        }

        foreach (var inner in ComputationLambdas.Descend(helper.Body))
        {
            InspectCapture(context, classifier, inner, helper.Scope, patchScope, semanticModel, reported);
            InspectCapturedLocalFunction(context, classifier, inner, patchScope, semanticModel, visited, reported);
        }
    }

    // Any same-tree method or local function on the patch's own execution path is chased for
    // STATIC reads: a static is shared however deep the call chain that reaches it, and the
    // instance verdicts (receiver, bare `this`) already cover the object the helper runs on --
    // but the classifier deliberately ignores statics, so `int ReadHits() => hits;` on a
    // Sendable type would otherwise hide what the direct read flags. Metadata bodies stay
    // unwalkable, like every same-tree-only resolution here.
    private static void InspectExecutedHelper(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        SemanticModel? semanticModel,
        HashSet<(SyntaxNode, IMethodSymbol)> visitedCalls,
        HashSet<SyntaxNode> visitedInvokes,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        HashSet<ISymbol> reported)
    {
        if (operation is IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke, Instance: { } callee })
        {
            // A delegate PARAMETER invoked by a chased helper resolves through the call-site
            // arguments: `Run(() => hits)` with `Run(Func<int> f) => f()` executes the lambda.
            var resolvedCallee = ComputationLambdas.SubstituteArguments(ResolveConditionalReceiver(callee), argumentMap) ?? callee;
            InspectInvokedDelegate(context, classifier, resolvedCallee, semanticModel, visitedCalls, visitedInvokes, argumentMap, reported);
            return;
        }

        // Only what EXECUTES is chased -- calls, property accessors by usage (an assignment
        // target runs the SETTER, where a hidden static read replays too), constructors,
        // user-defined operators -- each runs on every replay of the stored patch. A method
        // group the patch stores builds a delegate without running it -- the same deferred
        // shape as a built lambda, which this walk already prunes. (The capture chase above
        // keeps method references: a lifted local function's closure is pinned either way.
        // The nameof check runs BEFORE the visited add: a member mentioned only in nameof is
        // neither executed nor captured, and it must not poison the visited set for a later
        // real use.)
        foreach (var method in ComputationLambdas.ExecutedMethods(operation))
        {
            if (ComputationLambdas.IsInsideNameOf(operation) || !visitedCalls.Add((operation.Syntax, method))
                || ComputationLambdas.ResolveMethodBody(method, semanticModel) is not { } helper)
            {
                continue;
            }

            var nestedMap = ComputationLambdas.BuildArgumentMap(operation, argumentMap);
            foreach (var inner in ComputationLambdas.DescendDirectExecution(helper.Body))
            {
                InspectStaticRead(context, classifier, inner, reported);
                InspectExecutedHelper(context, classifier, inner, semanticModel, visitedCalls, visitedInvokes, nestedMap, reported);
            }
        }
    }

    // A delegate the patch builds AND synchronously invokes runs its body on every replay:
    // the built-but-deferred shape stays pruned, but `Func<int> later = () => hits; later()`
    // executes now, and unlike MZR003's identical prune there is no runtime backstop. The
    // callee resolves like a computation argument (same-tree lambda or method group, through
    // variable initializers). The guard is per INVOCATION SITE: a delegate reassigned between
    // two invokes executes a different closure at each, while a self-recursive delegate
    // re-reaches the same site inside its own body and stops there.
    private static void InspectInvokedDelegate(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation callee,
        SemanticModel? semanticModel,
        HashSet<(SyntaxNode, IMethodSymbol)> visitedCalls,
        HashSet<SyntaxNode> visitedInvokes,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        HashSet<ISymbol> reported)
    {
        if (!visitedInvokes.Add(callee.Syntax))
        {
            return;
        }

        foreach (var body in ComputationLambdas.OfArgumentValue(callee, semanticModel))
        {
            ChaseExecutedBody(context, classifier, body, semanticModel, visitedCalls, visitedInvokes, argumentMap, reported);
        }

        var variable = ComputationLambdas.ReferencedVariable(Unwrap(callee));
        if (variable is null || semanticModel is null)
        {
            return;
        }

        foreach (var body in AssignedDelegateBodies(variable, callee.Syntax, semanticModel))
        {
            ChaseExecutedBody(context, classifier, body, semanticModel, visitedCalls, visitedInvokes, argumentMap, reported);
        }
    }

    // `later?.Invoke()` surfaces the invoke receiver as the conditional-access placeholder;
    // the real delegate expression is the enclosing conditional access's Operation.
    private static IOperation ResolveConditionalReceiver(IOperation callee)
    {
        if (callee is IConditionalAccessInstanceOperation placeholder)
        {
            for (var current = placeholder.Parent; current is not null; current = current.Parent)
            {
                if (current is IConditionalAccessOperation conditional)
                {
                    return conditional.Operation;
                }
            }
        }

        return callee;
    }

    // A delegate assembled by ASSIGNMENT (`Func<int> later; later = () => hits;`, including
    // through deconstruction) has no initializer to resolve, so every same-tree assignment's
    // right-hand side that can EXECUTE before the invocation is a body that might be the one
    // invoked. Deconstructions pair positionally: `(other, later) = (a, b)` assigns only `b`
    // to `later`, so `a` must not be chased.
    private static IEnumerable<ComputationLambdas.ComputationBody> AssignedDelegateBodies(ISymbol variable, SyntaxNode invokeSite, SemanticModel semanticModel)
    {
        var writes = new List<SyntaxNode>();
        foreach (var node in semanticModel.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (WritesDelegateBeforeInvoke(node, variable, invokeSite, semanticModel))
            {
                writes.Add(node);
            }
        }

        foreach (var node in writes)
        {
            // A later straight-line simple assignment on the path to the invoke REPLACES the
            // value: the earlier write's body can never be the one invoked.
            if (writes.Any(other => other != node && DefinitelyOverwrites(other, node, invokeSite, variable, semanticModel)))
            {
                continue;
            }

            foreach (var body in NodeAssignedBodies(node, variable, semanticModel))
            {
                yield return body;
            }
        }
    }

    private static bool WritesDelegateBeforeInvoke(SyntaxNode node, ISymbol variable, SyntaxNode invokeSite, SemanticModel semanticModel)
    {
        return node switch
        {
            AssignmentExpressionSyntax assignment =>
                ComputationLambdas.ReassignmentTargets(assignment) is { } targets
                && targets.Any(target => ComputationLambdas.WritesVariable(target, variable, semanticModel))
                && ComputationLambdas.CanExecuteBefore(assignment, invokeSite, variable, semanticModel),
            ArgumentSyntax argument =>
                argument.RefOrOutKeyword.Kind() is SyntaxKind.OutKeyword or SyntaxKind.RefKeyword
                && semanticModel.GetSymbolInfo(argument.Expression).Symbol is { } written
                && SymbolEqualityComparer.Default.Equals(written, variable)
                && ComputationLambdas.CanExecuteBefore(argument, invokeSite, variable, semanticModel),
            _ => false,
        };
    }

    // "Straight-line" = the killer sits in plain block statements between itself and wherever
    // the invoke lives; a killer inside an if/loop may not run, so it kills nothing.
    private static bool DefinitelyOverwrites(SyntaxNode killer, SyntaxNode victim, SyntaxNode invokeSite, ISymbol variable, SemanticModel semanticModel)
    {
        if (killer is not AssignmentExpressionSyntax assignment
            || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            || assignment.Left is TupleExpressionSyntax
            || !ComputationLambdas.WritesVariable(assignment.Left, variable, semanticModel)
            || killer.SpanStart <= victim.SpanStart
            || killer.SpanStart >= invokeSite.SpanStart)
        {
            return false;
        }

        for (var current = killer.Parent; current is not null && !current.Span.Contains(invokeSite.Span); current = current.Parent)
        {
            if (current is not (BlockSyntax or ExpressionStatementSyntax))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<ComputationLambdas.ComputationBody> NodeAssignedBodies(SyntaxNode node, ISymbol variable, SemanticModel semanticModel)
    {
        switch (node)
        {
            case AssignmentExpressionSyntax assignment:
                foreach (var value in AssignedValuesFor(assignment.Left, assignment.Right, variable, semanticModel))
                {
                    foreach (var body in ComputationLambdas.OfArgumentValue(value, semanticModel))
                    {
                        yield return body;
                    }
                }

                break;

            // `Provide(out later)` assembles the delegate inside the callee: the bodies come
            // from the helper's assignments to its out/ref parameter.
            case ArgumentSyntax argument:
                foreach (var body in OutAssignedBodies(argument, semanticModel))
                {
                    yield return body;
                }

                break;
        }
    }

    private static IEnumerable<ComputationLambdas.ComputationBody> OutAssignedBodies(ArgumentSyntax argument, SemanticModel semanticModel)
    {
        // The SEMANTIC parameter, not the syntactic position: named arguments reorder freely
        // (`Provide(second: out later, first: 0)`).
        if ((semanticModel.GetOperation(argument) as IArgumentOperation)?.Parameter is not { } parameter
            || parameter.ContainingSymbol is not IMethodSymbol method
            || ComputationLambdas.ResolveMethodBody(method, semanticModel) is not { } helper)
        {
            yield break;
        }

        foreach (var node in helper.Scope.DescendantNodes())
        {
            if (node is not AssignmentExpressionSyntax assignment
                || !SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(assignment.Left).Symbol, parameter)
                || semanticModel.GetOperation(assignment.Right) is not { } assigned)
            {
                continue;
            }

            foreach (var body in ComputationLambdas.OfArgumentValue(assigned, semanticModel))
            {
                yield return body;
            }
        }
    }

    private static IEnumerable<IOperation> AssignedValuesFor(ExpressionSyntax left, ExpressionSyntax right, ISymbol variable, SemanticModel semanticModel)
    {
        if (left is TupleExpressionSyntax leftTuple && right is TupleExpressionSyntax rightTuple
            && leftTuple.Arguments.Count == rightTuple.Arguments.Count)
        {
            for (var i = 0; i < leftTuple.Arguments.Count; i++)
            {
                foreach (var value in AssignedValuesFor(leftTuple.Arguments[i].Expression, rightTuple.Arguments[i].Expression, variable, semanticModel))
                {
                    yield return value;
                }
            }

            yield break;
        }

        // WritesVariable, not plain symbol equality: an assignment through a ref alias
        // (`ref var alias = ref later; alias = () => hits;`) rebinds the delegate too.
        if (ComputationLambdas.WritesVariable(left, variable, semanticModel)
            && semanticModel.GetOperation(right) is { } operation)
        {
            yield return operation;
        }
    }

    private static void ChaseExecutedBody(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        ComputationLambdas.ComputationBody body,
        SemanticModel? semanticModel,
        HashSet<(SyntaxNode, IMethodSymbol)> visitedCalls,
        HashSet<SyntaxNode> visitedInvokes,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        HashSet<ISymbol> reported)
    {
        foreach (var inner in ComputationLambdas.DescendDirectExecution(body.Body))
        {
            InspectStaticRead(context, classifier, inner, reported);
            InspectExecutedHelper(context, classifier, inner, semanticModel, visitedCalls, visitedInvokes, argumentMap, reported);
        }
    }

    private static IMethodSymbol? ReferencedMethod(IOperation operation)
    {
        return operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IMethodReferenceOperation reference => reference.Method,
            _ => null,
        };
    }

    // Captured by the walked scope AND declared in a function that encloses the PATCH: only
    // then does the symbol live in the environment the patch's stored closure shares. A local
    // function nested inside a called helper also captures that helper's OWN locals -- but
    // those are recreated on every patch execution, not stored in the delegate, so they must
    // stay silent.
    private static bool IsSharedCapture(ISymbol symbol, SyntaxNode scope, SyntaxNode patchScope)
    {
        return ComputationLambdas.IsDeclaredOutside(symbol, scope)
            && ComputationLambdas.DeclaredInFunctionEnclosing(symbol, patchScope);
    }

    // A captured VARIABLE is shared storage, not a copy: the closure hoists the variable
    // itself into the display class. A mutable struct -- which the classifier accepts for node
    // VALUES precisely because those are copied -- is therefore writable shared state here:
    // the owner mutates `counter.Value` in place while re-executions re-read the same storage.
    // Immutable structs and Sendable reference types stay accepted as before.
    private static void ReportCapturedVariable(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        ISymbol symbol,
        ITypeSymbol type,
        HashSet<ISymbol> reported)
    {
        if (IsMutableStruct(type))
        {
            Report(
                context,
                operation,
                symbol,
                symbol.Name,
                $"its type '{SendableSymbolClassifier.Display(type)}' is a mutable struct, and the stored closure shares the captured variable's storage rather than a copy",
                reported);
            return;
        }

        ReportIfNotSendable(context, classifier, operation, symbol, type, reported);
    }

    // A value type with directly writable instance state. Enums have no user state (their
    // synthesized backing field must not count), and a metadata struct's private fields are
    // not imported -- benefit of the doubt, like everywhere else.
    private static bool IsMutableStruct(ITypeSymbol type)
    {
        if (!type.IsValueType || type.TypeKind == TypeKind.Enum || type is not INamedTypeSymbol named)
        {
            return false;
        }

        return named.GetMembers().Any(member =>
            member is IFieldSymbol { IsStatic: false, IsReadOnly: false, IsConst: false }
            || (member is IPropertySymbol { IsStatic: false } property && IsSettable(property)));
    }

    // The shared verdict for a state read: writable storage is flagged outright; immutable
    // storage is held to the member TYPE's sendability.
    private static void InspectStateRead(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        ISymbol member,
        ITypeSymbol type,
        bool writable,
        string writableProblem,
        HashSet<ISymbol> reported)
    {
        if (writable)
        {
            Report(context, operation, member, member.Name, writableProblem, reported);
        }
        else
        {
            ReportIfNotSendable(context, classifier, operation, member, type, reported);
        }
    }

    // An init accessor surfaces as SetMethod but cannot run after construction: `{ get; init; }`
    // is immutable state, held (like readonly fields) only to its TYPE's sendability -- the same
    // verdict the runtime SendableChecker gives it. A ref-RETURNING property is the reverse
    // disguise: no setter, yet `ref int Counter => ref counter` hands out assignable live
    // storage, so it counts as writable.
    private static bool IsSettable(IPropertySymbol property)
    {
        return property.SetMethod is { IsInitOnly: false } || property.RefKind == RefKind.Ref;
    }

    // True when the reference is the receiver of a member read InspectCapture handles itself:
    // reporting `this` under those too would double-count every ordinary `x => x + counter`.
    private static bool IsInspectedMemberReceiver(IInstanceReferenceOperation instance)
    {
        return (instance.Parent is IFieldReferenceOperation field && ReferenceEquals(field.Instance, instance))
            || (instance.Parent is IPropertyReferenceOperation property && ReferenceEquals(property.Instance, instance));
    }

    private static bool IsOnNonSendableEnclosingInstance(IOperation? receiver, SendableSymbolClassifier classifier)
    {
        return receiver is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, Type: { } type }
            && classifier.GetNotSendableReason(type) is not null;
    }

    private static void ReportIfNotSendable(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        ISymbol symbol,
        ITypeSymbol type,
        HashSet<ISymbol> reported)
    {
        var reason = classifier.GetNotSendableReason(type);
        if (reason is null)
        {
            return;
        }

        Report(
            context,
            operation,
            symbol,
            symbol.Name,
            $"its type '{SendableSymbolClassifier.Display(type)}' is not Sendable ({reason})",
            reported);
    }

    private static void Report(
        OperationAnalysisContext context,
        IOperation operation,
        ISymbol dedupeKey,
        string name,
        string problem,
        HashSet<ISymbol> reported)
    {
        if (ComputationLambdas.IsInsideNameOf(operation) || !reported.Add(dedupeKey))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.NonSendablePatchCapture,
            operation.Syntax.GetLocation(),
            name,
            problem));
    }

}
