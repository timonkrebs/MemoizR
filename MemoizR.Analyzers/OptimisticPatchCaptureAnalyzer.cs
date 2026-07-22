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

        var resolved = methodReference is not null;
        foreach (var patch in ComputationLambdas.OfArgumentValue(value, semanticModel))
        {
            resolved = true;
            InspectPatchBody(context, classifier, patch, semanticModel, reported);
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

    // Any assignment beyond the declaration initializer (including a ref/out handoff) that can
    // EXECUTE before this call: a same-tree syntactic scan, so a reassignment in another file
    // is residual risk like every same-tree-only resolution here. A later straight-line
    // assignment cannot change the delegate this call already stored -- flagging it would
    // punish the common rebind-after-use.
    private static bool IsReassignedBefore(ISymbol variable, SyntaxNode reference, SemanticModel? semanticModel)
    {
        if (semanticModel is null)
        {
            return false;
        }

        foreach (var node in semanticModel.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (ReassignmentTargets(node) is { } targets
                && targets.Any(target => WritesVariable(target, variable, semanticModel))
                && CanExecuteBefore(node, reference, variable))
            {
                return true;
            }
        }

        return false;
    }

    // The reassignment check applies to EVERY variable in the alias chain, each against the
    // site where its value is READ: `p0 = evil; Func<int,int> patch = p0;` stores p0's
    // post-reassignment closure even though patch itself is never written again. A link whose
    // initializer is the computation itself (a lambda or method group) ends the chain.
    private static ISymbol? FindReassignedLink(IOperation value, SemanticModel? semanticModel)
    {
        var reference = Unwrap(value);
        var site = value.Syntax;
        HashSet<ISymbol>? visited = null;
        while (ReceiverSymbol(reference) is (ILocalSymbol or IFieldSymbol or IPropertySymbol) and { } variable)
        {
            if (IsReassignedBefore(variable, site, semanticModel))
            {
                return variable;
            }

            visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (!visited.Add(variable)
                || ComputationLambdas.SameTreeInitializerOperation(variable, semanticModel) is not { } initializer)
            {
                return null;
            }

            site = initializer.Syntax;
            reference = Unwrap(initializer);
        }

        return null;
    }

    // A ref-local ALIAS writes its referent: `ref var alias = ref patch; alias = ...` rebinds
    // patch just as directly. The alias chain resolves through `= ref` initializers; the
    // visited set breaks alias cycles.
    private static bool WritesVariable(ExpressionSyntax target, ISymbol variable, SemanticModel semanticModel)
    {
        var symbol = semanticModel.GetSymbolInfo(target).Symbol;
        HashSet<ISymbol>? visited = null;
        while (true)
        {
            if (symbol is null)
            {
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(symbol, variable))
            {
                return true;
            }

            if (symbol is not ILocalSymbol { RefKind: RefKind.Ref })
            {
                return false;
            }

            visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (!visited.Add(symbol))
            {
                return false;
            }

            if (symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax
                {
                    Initializer.Value: RefExpressionSyntax { Expression: { } referent }
                })
            {
                return false;
            }

            symbol = semanticModel.GetSymbolInfo(referent).Symbol;
        }
    }

    // The expressions a node writes: an assignment's left-hand side (deconstruction tuples
    // flattened, nesting included -- `(patch, _) = ...` writes `patch`) or a ref/out argument.
    private static IEnumerable<ExpressionSyntax>? ReassignmentTargets(SyntaxNode node)
    {
        return node switch
        {
            AssignmentExpressionSyntax assignment => FlattenTargets(assignment.Left),
            // `in` is excluded: a readonly reference cannot rebind the variable.
            ArgumentSyntax argument when argument.RefOrOutKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                => new[] { argument.Expression },
            _ => null,
        };
    }

    private static IEnumerable<ExpressionSyntax> FlattenTargets(ExpressionSyntax left)
    {
        if (left is not TupleExpressionSyntax tuple)
        {
            yield return left;
            yield break;
        }

        foreach (var element in tuple.Arguments)
        {
            foreach (var nested in FlattenTargets(element.Expression))
            {
                yield return nested;
            }
        }
    }

    // Execution-order reasoning without dataflow: an assignment in a DIFFERENT function body
    // (a helper, a lambda, another method) runs whenever that function does -- unknowable, so
    // it counts. In the same function, one textually before the call obviously precedes it,
    // and one textually after still reaches the call when a loop encloses both (the next
    // iteration passes the reassigned delegate).
    private static bool CanExecuteBefore(SyntaxNode assignment, SyntaxNode reference, ISymbol variable)
    {
        if (!ReferenceEquals(EnclosingFunction(assignment), EnclosingFunction(reference)))
        {
            return true;
        }

        if (assignment.SpanStart < reference.SpanStart)
        {
            return true;
        }

        for (var current = assignment.Parent; current is not null; current = current.Parent)
        {
            // Loop-carried only when the variable OUTLIVES the iteration: a delegate local
            // declared inside the loop body is freshly initialized each pass, so a trailing
            // reassignment dies with its iteration and can never reach a call.
            if (current is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                && current.Span.Contains(reference.Span)
                && !DeclaredWithin(variable, current))
            {
                return true;
            }
        }

        return false;
    }

    // Only the loop BODY re-declares per iteration: a variable in a for-INITIALIZER is
    // declared once and carries across iterations like one declared before the loop.
    private static bool DeclaredWithin(ISymbol variable, SyntaxNode loop)
    {
        var body = loop switch
        {
            ForStatementSyntax @for => (SyntaxNode)@for.Statement,
            ForEachStatementSyntax forEach => forEach.Statement,
            WhileStatementSyntax @while => @while.Statement,
            DoStatementSyntax @do => @do.Statement,
            _ => loop,
        };

        var declaration = variable.DeclaringSyntaxReferences.FirstOrDefault();
        return declaration is not null
            && declaration.SyntaxTree == body.SyntaxTree
            && body.Span.Contains(declaration.Span);
    }

    private static SyntaxNode? EnclosingFunction(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
                or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or ArrowExpressionClauseSyntax)
            {
                return current;
            }
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
        HashSet<ISymbol> reported)
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
            InspectCapture(context, classifier, operation, patch.Scope, patch.Scope, reported);
            InspectCapturedLocalFunction(context, classifier, operation, patch.Scope, semanticModel, visitedLocalFunctions, reported);
        }

        // ISymbol, not IMethodSymbol: the executed chase also visits invoked delegate VARIABLES.
        var visitedHelpers = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var operation in ComputationLambdas.DescendDirectExecution(patch.Body))
        {
            InspectStaticRead(context, classifier, operation, reported);
            InspectExecutedHelper(context, classifier, operation, semanticModel, visitedHelpers, reported);
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
            InspectCapture(context, classifier, inner, helper.Scope, patchScope, reported);
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
        HashSet<ISymbol> visited,
        HashSet<ISymbol> reported)
    {
        if (operation is IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke, Instance: { } callee })
        {
            InspectInvokedDelegate(context, classifier, callee, semanticModel, visited, reported);
            return;
        }

        // Only what EXECUTES is chased: a call; a property READ, which runs its getter exactly
        // like a call (`static int Hits => hits;` is the helper-method evasion with property
        // syntax); a constructor; or a user-defined operator/conversion -- each runs on every
        // replay of the stored patch. A method group the patch stores builds a delegate
        // without running it -- the same deferred shape as a built lambda, which this walk
        // already prunes. (The capture chase above keeps method references: a lifted local
        // function's closure is pinned either way.)
        var method = operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IPropertyReferenceOperation property => property.Property.GetMethod,
            IObjectCreationOperation creation => creation.Constructor,
            IBinaryOperation { OperatorMethod: { } binaryOperator } => binaryOperator,
            IUnaryOperation { OperatorMethod: { } unaryOperator } => unaryOperator,
            IConversionOperation { OperatorMethod: { } conversionOperator } => conversionOperator,
            _ => null,
        };

        // The nameof check runs BEFORE the visited add: a property mentioned only in nameof is
        // neither executed nor captured, and it must not poison the visited set for a later
        // real read of the same member.
        if (method is null || ComputationLambdas.IsInsideNameOf(operation) || !visited.Add(method)
            || ComputationLambdas.ResolveMethodBody(method, semanticModel) is not { } helper)
        {
            return;
        }

        foreach (var inner in ComputationLambdas.DescendDirectExecution(helper.Body))
        {
            InspectStaticRead(context, classifier, inner, reported);
            InspectExecutedHelper(context, classifier, inner, semanticModel, visited, reported);
        }
    }

    // A delegate the patch builds AND synchronously invokes runs its body on every replay:
    // the built-but-deferred shape stays pruned, but `Func<int> later = () => hits; later()`
    // executes now, and unlike MZR003's identical prune there is no runtime backstop. The
    // callee resolves like a computation argument (same-tree lambda or method group, through
    // variable initializers); the variable visited-guard bounds self-referential delegates.
    private static void InspectInvokedDelegate(
        OperationAnalysisContext context,
        SendableSymbolClassifier classifier,
        IOperation callee,
        SemanticModel? semanticModel,
        HashSet<ISymbol> visited,
        HashSet<ISymbol> reported)
    {
        var variable = ComputationLambdas.ReferencedVariable(Unwrap(callee));
        if (variable is not null && !visited.Add(variable))
        {
            return;
        }

        foreach (var body in ComputationLambdas.OfArgumentValue(callee, semanticModel))
        {
            foreach (var inner in ComputationLambdas.DescendDirectExecution(body.Body))
            {
                InspectStaticRead(context, classifier, inner, reported);
                InspectExecutedHelper(context, classifier, inner, semanticModel, visited, reported);
            }
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
