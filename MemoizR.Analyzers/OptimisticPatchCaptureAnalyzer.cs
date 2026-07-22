using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
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
        var methodReference = ResolveMethodReference(value, semanticModel, visitedVariables: null);
        if (methodReference is not null)
        {
            InspectMethodGroupReceiver(context, classifier, methodReference, reported);
        }

        var resolved = methodReference is not null;
        var visitedHelpers = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var patch in ComputationLambdas.OfArgumentValue(value, semanticModel))
        {
            resolved = true;
            foreach (var operation in ComputationLambdas.Descend(patch.Body))
            {
                InspectCapture(context, classifier, operation, patch.Scope, reported);
                InspectStaticRead(context, classifier, operation, reported);
                InspectCalledHelper(context, classifier, operation, patch.Scope, semanticModel, visitedHelpers, reported);
            }
        }

        if (!resolved && value.Type is { TypeKind: TypeKind.Delegate })
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
        HashSet<ISymbol> reported)
    {
        switch (operation)
        {
            case ILocalReferenceOperation local when ComputationLambdas.IsDeclaredOutside(local.Local, scope):
                ReportIfNotSendable(context, classifier, operation, local.Local, local.Local.Type, reported);
                break;
            case IParameterReferenceOperation parameter when ComputationLambdas.IsDeclaredOutside(parameter.Parameter, scope):
                ReportIfNotSendable(context, classifier, operation, parameter.Parameter, parameter.Parameter.Type, reported);
                break;

            // A member access on the enclosing object captures `this`. The per-member verdicts
            // REFINE a non-Sendable enclosing object -- they say which reads make sharing it
            // dangerous: writable storage is flagged outright (the patch re-reads it on other
            // flows while the owner mutates it freely); readonly/get-only members are held to
            // the member TYPE's sendability (the object handed out is what gets shared). An
            // enclosing type the classifier accepts ([Sendable], structurally immutable) was
            // vetted WHOLESALE -- re-walking its members would override that trust, exactly
            // where the runtime SendableChecker trusts it. A computed get-only property's body
            // is not chased: like type parameters elsewhere, it gets the benefit of the doubt.
            case IFieldReferenceOperation { Field.IsStatic: false } field when IsOnNonSendableEnclosingInstance(field.Instance, classifier):
                InspectStateRead(
                    context, classifier, operation, field.Field, field.Field.Type,
                    writable: !field.Field.IsReadOnly, "it is writable state of the enclosing object", reported);
                break;
            case IPropertyReferenceOperation { Property.IsStatic: false } property when IsOnNonSendableEnclosingInstance(property.Instance, classifier):
                InspectStateRead(
                    context, classifier, operation, property.Property, property.Property.Type,
                    writable: IsSettable(property.Property), "it is writable state of the enclosing object", reported);
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
                InspectStateRead(
                    context, classifier, operation, staticProperty.Property, staticProperty.Property.Type,
                    writable: IsSettable(staticProperty.Property), "it is writable static state", reported);
                break;
        }
    }

    // A same-tree method the patch calls (or lifts into a delegate) is chased for STATIC reads
    // only: a static is shared however deep the call chain that reaches it, and the instance
    // verdicts (receiver, bare `this`) already cover the object the helper runs on -- but the
    // classifier deliberately ignores statics, so `int ReadHits() => hits;` on a Sendable type
    // would otherwise hide what the direct read flags. Metadata bodies stay unwalkable, like
    // every same-tree-only resolution here; the visited set bounds recursion on call cycles.
    private static void InspectCalledHelper(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation operation,
        SyntaxNode patchScope,
        SemanticModel? semanticModel,
        HashSet<IMethodSymbol> visitedHelpers,
        HashSet<ISymbol> reported)
    {
        var method = operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IMethodReferenceOperation reference => reference.Method,
            _ => null,
        };

        if (method is null || !visitedHelpers.Add(method)
            || ComputationLambdas.ResolveMethodBody(method, semanticModel) is not { } helper)
        {
            return;
        }

        // A LOCAL FUNCTION has no receiver, so no receiver/`this` verdict covers its closure:
        // one declared outside the patch shares the enclosing method's locals with the patch,
        // and what its body reads from outside its own declaration is captured state, held to
        // the ordinary capture verdicts. (One declared INSIDE the patch was already walked
        // under the patch's own scope, and its reads of patch-locals are patch-internal, not
        // shared -- re-inspecting it against its own scope would flag them falsely.)
        var inspectCaptures = method.MethodKind == MethodKind.LocalFunction
            && ComputationLambdas.IsDeclaredOutside(method, patchScope);

        foreach (var inner in ComputationLambdas.Descend(helper.Body))
        {
            if (inspectCaptures)
            {
                InspectCapture(context, classifier, inner, helper.Scope, reported);
            }

            InspectStaticRead(context, classifier, inner, reported);
            InspectCalledHelper(context, classifier, inner, patchScope, semanticModel, visitedHelpers, reported);
        }
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
        if (!reported.Add(dedupeKey))
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
