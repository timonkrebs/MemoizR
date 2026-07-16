using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// MZR005: a value wrapped in Sending<T> is TRANSFERRED -- the receiver may start owning and
// mutating it on another flow the moment the wrapper escapes, so the sender must stop using it.
// The check is method-local and source-ordered: every reference to the transferred local/
// parameter AFTER the transfer is flagged, until a reassignment gives the variable a fresh
// value. A transfer inside a nested callback is scoped to that callback's own body -- the
// outer flow is not sequenced after code that may run later or never.
// Deliberately a heuristic (source order approximates execution order; loop back-edges
// and aliases can evade it) -- Swift proves this with region-based isolation in the type
// system, which an analyzer cannot reproduce; the single-consumption check in Sending<T> is the
// runtime backstop on the RECEIVER side.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseAfterTransferAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.UseAfterTransfer);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockAction(AnalyzeBlock);
    }

    private static void AnalyzeBlock(OperationBlockAnalysisContext context)
    {
        foreach (var block in context.OperationBlocks)
        {
            foreach (var (variable, transferPosition, scanRoots, escaped, transfer) in Transfers(block))
            {
                // A using-declared local -- or an existing local/parameter handed to a using
                // STATEMENT as its resource -- is Disposed by the SENDER at scope end, after
                // the handoff, with no source reference to scan for: destroying the object the
                // receiver now owns is a guaranteed use-after-transfer.
                if (variable is ILocalSymbol { IsUsing: true }
                    || IsDisposedByAnEnclosingUsing(transfer, variable)
                    || IsIteratedByAnEnclosingForeach(transfer, transferPosition, variable))
                {
                    Report(context, transfer, variable);
                    continue;
                }

                // list = MakeFresh(Sending.Transfer(list)): the assignment ENCLOSING the
                // transfer completes right after the RHS, reinitializing the variable -- only
                // reads still inside the RHS (after the transfer) and the throw window count.
                if (EnclosingReinitializingAssignment(transfer, variable) is { } enclosingReinit)
                {
                    var scope = scanRoots[scanRoots.Count - 1];
                    if (!ReportUsesAfter(context, new List<IOperation> { enclosingReinit }, escaped, variable, transferPosition)
                        && CatchUseDuringReinitialization(enclosingReinit, variable, scope) is { } windowUse)
                    {
                        Report(context, windowUse, variable);
                    }

                    continue;
                }

                ReportUsesAfter(context, scanRoots, escaped, variable, transferPosition);
            }
        }
    }

    // Every Sending<T> creation (constructor or Sending.Transfer) whose argument is a
    // local/parameter reference, with the position the transfer happens at and the regions the
    // report walk covers.
    private static IEnumerable<(ISymbol Variable, int Position, List<IOperation> ScanRoots, bool Escaped, IOperation Transfer)> Transfers(IOperation block)
    {
        foreach (var operation in block.DescendantsAndSelf())
        {
            var (isTransfer, argument) = operation switch
            {
                IObjectCreationOperation creation when IsSendingType(creation.Type) =>
                    (true, creation.Arguments.FirstOrDefault()?.Value),
                IInvocationOperation { TargetMethod.Name: "Transfer" } invocation when IsSendingHelper(invocation.TargetMethod) =>
                    (true, invocation.Arguments.FirstOrDefault()?.Value),
                _ => (false, null),
            };

            if (!isTransfer)
            {
                continue;
            }

            foreach (var source in TransferSources(argument))
            {
                if (ReferencedVariable(source) is not { } variable)
                {
                    continue;
                }

                var (scanRoots, escaped) = ScanRootsFor(operation, EnclosingFunctionBody(operation, block));
                if (scanRoots.Count > 0)
                {
                    yield return (variable, operation.Syntax.Span.End, scanRoots, escaped, operation);
                }
            }
        }
    }

    private static ISimpleAssignmentOperation? EnclosingReinitializingAssignment(IOperation transfer, ISymbol variable)
    {
        for (IOperation child = transfer; child.Parent is { } parent; child = parent)
        {
            if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                break;
            }

            if (parent is ISimpleAssignmentOperation assignment
                && !ReferenceEquals(child, assignment.Target)
                && SymbolEqualityComparer.Default.Equals(ReferencedVariable(assignment.Target), variable))
            {
                return assignment;
            }
        }

        return null;
    }

    private static bool IsDisposedByAnEnclosingUsing(IOperation transfer, ISymbol variable)
    {
        for (var parent = transfer.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                break; // a using outside the callback disposes on the OUTER flow's schedule
            }

            // Only a resource that IS the variable disposes the handoff: `using (stream)`.
            // A resource merely mentioning it (`using (new Scope(list.Count))`) disposes the
            // wrapper, not the transferred object. A `lock (gate)` around the transfer is the
            // same shape: the compiler-generated Monitor.Exit touches the handed-off object
            // at scope end.
            var scopeExitTarget = parent switch
            {
                IUsingOperation usingOperation => usingOperation.Resources,
                ILockOperation lockOperation => lockOperation.LockedValue,
                _ => null,
            };

            if (scopeExitTarget is not null
                && SymbolEqualityComparer.Default.Equals(ReferencedVariable(scopeExitTarget), variable))
            {
                return true;
            }
        }

        return false;
    }

    // An enclosing foreach KEEPS READING its collection after the body iteration that
    // performed the handoff: the next MoveNext is a sender-side use with no source reference
    // -- unless the transfer's continuation definitely leaves the loop (a break/return/throw
    // on the transfer's own conditional level).
    private static bool IsIteratedByAnEnclosingForeach(IOperation transfer, int transferPosition, ISymbol variable)
    {
        for (var parent = transfer.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                break;
            }

            if (parent is IForEachLoopOperation foreachLoop
                && SymbolEqualityComparer.Default.Equals(ReferencedVariable(foreachLoop.Collection), variable)
                && !DefinitelyExitsAfter(foreachLoop, transferPosition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DefinitelyExitsAfter(IForEachLoopOperation loop, int transferPosition)
    {
        return loop.Body.DescendantsAndSelf().Any(operation =>
            operation.Syntax.SpanStart >= transferPosition
            && !IsWithinANestedFunction(operation, loop.Body)
            && ExitsTheLoop(operation, loop)
            && IsOnTheTransfersConditionalLevel(operation, transferPosition));
    }

    // A break only ends the ITERATION when it leaves THIS foreach: a break belonging to a
    // nested switch or inner loop resumes inside the body, and the next MoveNext still runs.
    private static bool ExitsTheLoop(IOperation operation, IForEachLoopOperation loop)
    {
        // yield return only SUSPENDS the iterator: the next MoveNext resumes inside the loop,
        // so the foreach keeps iterating (yield break, like return, ends it for good).
        if (operation is IReturnOperation { Kind: not OperationKind.YieldReturn } or IThrowOperation)
        {
            return true;
        }

        if (operation is not IBranchOperation { BranchKind: BranchKind.Break } branch)
        {
            return false;
        }

        for (var parent = branch.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is ILoopOperation or ISwitchOperation)
            {
                return ReferenceEquals(parent, loop);
            }
        }

        return false;
    }

    // The variables an argument expression can HAND OFF. An assignment transfers its target
    // (Transfer(list = new(...)) / Transfer(list ??= new(...)): the variable aliases the value
    // afterwards); a null-coalescing or conditional expression transfers whichever operand the
    // runtime picks (Transfer(list ?? fallback), Transfer(c ? a : b)) -- each is a
    // MAY-transfer, so each is tracked.
    private static System.Collections.Generic.IEnumerable<IOperation> TransferSources(IOperation? argument)
    {
        switch (argument)
        {
            case IAssignmentOperation assignment:
                // Transfer(list = other): BOTH list and other alias the handed-off object.
                yield return assignment.Target;
                foreach (var source in TransferSources(assignment.Value))
                {
                    yield return source;
                }

                break;
            case not null:
                var parts = CarriedParts(argument);
                if (parts is null)
                {
                    yield return argument; // a leaf: the expression itself is the source
                    break;
                }

                foreach (var source in parts.SelectMany(TransferSources))
                {
                    yield return source;
                }

                break;
        }
    }

    // The sub-expressions a compound argument carries into the handoff: whichever operand the
    // runtime picks for ??/?:/switch expressions, EVERY element of a tuple, array or anonymous
    // object (Transfer((list, 0)) / Transfer(new[] { list }) / Transfer(new { Value = list }):
    // the container deterministically stores its contents, unlike an arbitrary object
    // initializer's setters), and a conversion's operand. Null marks a leaf.
    private static System.Collections.Generic.IEnumerable<IOperation?>? CarriedParts(IOperation argument)
    {
        return argument switch
        {
            ICoalesceOperation coalesce => new[] { coalesce.Value, coalesce.WhenNull },
            IConditionalOperation { WhenFalse: { } whenFalse } conditional => new[] { conditional.WhenTrue, whenFalse },
            ISwitchExpressionOperation switchExpression => switchExpression.Arms.Select(arm => (IOperation?)arm.Value),
            ITupleOperation tuple => tuple.Elements,
            IArrayCreationOperation { Initializer: { } initializer } => initializer.ElementValues,
            IAnonymousObjectCreationOperation anonymous => anonymous.Initializers.Select(initializer =>
                (IOperation?)(initializer is ISimpleAssignmentOperation member ? member.Value : initializer)),
            IConversionOperation conversion => new[] { conversion.Operand },
            _ => null,
        };
    }

    // Where the sender-side scan looks. Normally the whole scope; a transfer the flow
    // immediately leaves behind (`return Sending.Transfer(list);` / `throw`) is only still
    // observable from the escaping expression itself, the CATCH handlers a thrown transfer
    // lands in, and the FINALLY blocks of enclosing tries -- no such region, no sender-side
    // continuation at all. (break/continue are NOT exits: control resumes after the loop,
    // where later uses remain reachable, so they keep the full scope. Neither is yield
    // return: the iterator resumes right after it on the next MoveNext.)
    private static (List<IOperation> Roots, bool Escaped) ScanRootsFor(IOperation transfer, IOperation scope)
    {
        var roots = new List<IOperation>();
        var escape = default(IOperation);
        for (IOperation child = transfer; child.Parent is { } parent; child = parent)
        {
            if (ReferenceEquals(parent, scope) || parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                break;
            }

            if (parent is IReturnOperation { Kind: not OperationKind.YieldReturn } or IThrowOperation)
            {
                if (escape is null)
                {
                    // The escaping expression itself still evaluates past the transfer:
                    // `return Pair(Sending.Transfer(list), list);` reads the second argument
                    // after the handoff, before the method returns.
                    roots.Add(parent);
                }

                escape = parent;
            }
            else if (escape is not null && parent is ITryOperation tryOperation)
            {
                // A return whose expression can throw AFTER building the wrapper
                // (return MayThrow(Sending.Transfer(list));) reaches the handlers like a
                // thrown transfer does.
                var reachesHandlers = escape is IThrowOperation
                    || CanThrowAfterTheTransfer(escape, transfer.Syntax.Span.End);
                AddHandlerRoots(roots, tryOperation, child, escapedByThrow: reachesHandlers);
            }
        }

        if (escape is null)
        {
            roots.Add(scope);
        }

        return (roots, escape is not null);
    }

    // A THROWN transfer lands in the try's handlers -- when the throw came from the try BODY
    // (a throw inside one catch never reaches its sibling catches; which handler matches is
    // not decidable statically, so every candidate is scanned). Finallys run for return and
    // throw alike: catches before finally, inner tries first.
    private static void AddHandlerRoots(List<IOperation> roots, ITryOperation tryOperation, IOperation child, bool escapedByThrow)
    {
        if (escapedByThrow && ReferenceEquals(child, tryOperation.Body))
        {
            roots.AddRange(tryOperation.Catches);
        }

        if (tryOperation.Finally is { } finallyBlock)
        {
            roots.Add(finallyBlock);
        }
    }

    // A transfer inside a nested callback only concerns the callback's own body: the outer
    // flow is not sequenced after it (the callback may run later or never), so its transfer
    // must not poison outer references. A transfer in the OUTER body rightly covers uses
    // inside callbacks defined after it -- if they run at all, they run after the transfer.
    private static IOperation EnclosingFunctionBody(IOperation operation, IOperation block)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return parent;
            }
        }

        return block;
    }

    private static bool ReportUsesAfter(OperationBlockAnalysisContext context, List<IOperation> scanRoots, bool escaped, ISymbol variable, int transferPosition)
    {
        var scope = scanRoots[scanRoots.Count - 1]; // the outermost root is the reinit scope boundary

        // Source-ordered walk of every later reference. References in a MUTUALLY EXCLUSIVE
        // sibling arm of the construct holding the transfer are excluded upfront: in
        // `if (move) Transfer(list); else list.Add(1);` no path runs the else after the
        // transfer. (Try constructs are not excluded -- an exception after the transfer
        // reaches the handlers and finally.)
        var laterReferences = scanRoots
            .SelectMany(root => root.DescendantsAndSelf())
            .Where(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                && operation.Syntax.SpanStart >= transferPosition
                && !IsInsideNameOf(operation)
                // A local-function DECLARATION does not execute: its body's references count
                // only through actual invocations (handled below). Lambda bodies stay direct
                // references -- a delegate built after the transfer escapes into code that
                // can only run after it.
                && !IsWithinALocalFunctionBody(operation, scanRoots)
                && !IsInASiblingArmOfTheTransfer(operation, transferPosition)
                && (escaped || !IsInAnUnreachableCatch(operation, transferPosition)))
            .Select(operation => (Operation: operation, IsLocalFunctionCall: false));

        // A call to a local function whose body reads the variable is a use AT THE CALL: the
        // body's reference sits source-BEFORE the transfer when the function is declared
        // earlier, so the position filter alone would hide it.
        var localFunctionCalls = scanRoots
            .SelectMany(root => root.DescendantsAndSelf())
            .OfType<IInvocationOperation>()
            .Where(invocation => invocation.TargetMethod.MethodKind == MethodKind.LocalFunction
                && invocation.Syntax.SpanStart >= transferPosition
                && !IsInASiblingArmOfTheTransfer(invocation, transferPosition)
                && (escaped || !IsInAnUnreachableCatch(invocation, transferPosition))
                && LocalFunctionReads(invocation.TargetMethod, variable, scope))
            .Select(invocation => (Operation: (IOperation)invocation, IsLocalFunctionCall: true));

        var orderedUses = laterReferences.Concat(localFunctionCalls)
            .OrderBy(use => use.Operation.Syntax.SpanStart);

        // Regions where a SKIPPED conditional reinitialization dominates: inside its own arm,
        // after it, the variable is fresh on every path that reaches the reference
        // (`if (reset) { list = new(); list.Add(1); }` -- the Add never sees the transferred
        // list), so such references are clean while the scan continues past the arm.
        List<(Microsoft.CodeAnalysis.Text.TextSpan Region, int Position)>? dominated = null;

        foreach (var (reference, isLocalFunctionCall) in orderedUses)
        {
            if (IsDominatedByASkippedReinitialization(dominated, reference))
            {
                continue;
            }

            if (isLocalFunctionCall)
            {
                Report(context, reference, variable);
                return true;
            }

            switch (Classify(reference, variable, transferPosition, scope, out var rhsUse))
            {
                case ReferenceRole.FreshValueFromHere:
                    // A throwing RHS/out-call can reach an enclosing catch AFTER the handoff
                    // but BEFORE the reinitialization completed: the handler still sees the
                    // transferred value on that path.
                    if (CatchUseDuringReinitialization(reference.Parent!, variable, scope) is { } windowUse)
                    {
                        Report(context, windowUse, variable);
                        return true;
                    }

                    return false; // a definite reinitialization: everything after is a new value
                case ReferenceRole.ConditionalReinitialization:
                    (dominated ??= new List<(Microsoft.CodeAnalysis.Text.TextSpan, int)>())
                        .Add((DominatingRegion(reference.Parent!).Syntax.Span, reference.Syntax.Span.End));
                    continue; // may not have run on the path that transferred: keep scanning
                default:
                    Report(context, rhsUse ?? reference, variable);
                    return true; // one report per transfer keeps the noise proportional
            }
        }

        return false;
    }

    private static bool IsDominatedByASkippedReinitialization(List<(Microsoft.CodeAnalysis.Text.TextSpan Region, int Position)>? dominated, IOperation reference)
    {
        if (dominated is null)
        {
            return false;
        }

        var start = reference.Syntax.SpanStart;
        foreach (var (region, position) in dominated)
        {
            if (start >= position && region.Contains(start))
            {
                return true;
            }
        }

        return false;
    }

    // A catch handler observes a NON-ESCAPING transfer only through an exception thrown after
    // it: when the transfer is the LAST operation of its try body, a completed handoff skips
    // the handlers, and a throw from the transfer expression itself means no wrapper escaped.
    // (Escaping thrown transfers scan their handlers deliberately: the throw carries the
    // completed wrapper into them.)
    private static bool IsInAnUnreachableCatch(IOperation use, int transferPosition)
    {
        for (IOperation child = use; child.Parent is { } parent; child = parent)
        {
            if (parent is ITryOperation { Body: { } body } && child is ICatchClauseOperation
                && Covers(body, transferPosition)
                && !body.Descendants().Any(operation => operation.Syntax.SpanStart >= transferPosition
                    && !IsWithinANestedFunction(operation, body))
                && !CanThrowAfterTheTransfer(body, transferPosition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWithinALocalFunctionBody(IOperation operation, List<IOperation> scanRoots)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is ILocalFunctionOperation)
            {
                return true;
            }

            if (scanRoots.Contains(current))
            {
                return false;
            }
        }

        return false;
    }

    // Whether CALLING the local function reads the transferred value: the body is scanned in
    // source order. A reassignment (with a non-reading RHS) that DEFINITELY runs before any
    // read means the call only ever touches the fresh value; a CONDITIONAL one
    // (`if (reset) list = new();`) may be skipped, so later reads still count -- except the
    // ones it dominates (same arm, after it), which read the value it wrote.
    private static bool LocalFunctionReads(IMethodSymbol localFunction, ISymbol variable, IOperation scope)
    {
        var declaration = scope.DescendantsAndSelf().OfType<ILocalFunctionOperation>()
            .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(operation.Symbol, localFunction));
        if (declaration is null)
        {
            return false;
        }

        var references = declaration.DescendantsAndSelf()
            .Where(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                && !IsInsideNameOf(operation))
            .OrderBy(operation => operation.Syntax.SpanStart);

        var dominated = default(List<(Microsoft.CodeAnalysis.Text.TextSpan Region, int Position)>);
        foreach (var reference in references)
        {
            if (IsDominatedByASkippedReinitialization(dominated, reference))
            {
                continue;
            }

            if (reference.Parent is not ISimpleAssignmentOperation assignment
                || !ReferenceEquals(assignment.Target, reference))
            {
                return true; // a plain read of the transferred value
            }

            if (assignment.Value.DescendantsAndSelf().Any(operation =>
                    SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)))
            {
                return true; // the RHS builds the replacement FROM the transferred value
            }

            if (!IsConditionalWithin(assignment, declaration)
                && !ABranchCanSkip(assignment, declaration.Syntax.SpanStart, declaration))
            {
                return false; // a definite reset: every call rewrites before any read
            }

            (dominated ??= new List<(Microsoft.CodeAnalysis.Text.TextSpan, int)>())
                .Add((DominatingRegion(assignment).Syntax.Span, reference.Syntax.Span.End));
        }

        return false;
    }

    // Whether control can reach past the operation without executing it, judged within the
    // local function's body: any conditional/loop/switch/try ancestor below the declaration
    // makes it skippable -- and a nested function's body is deferred entirely.
    private static bool IsConditionalWithin(IOperation operation, ILocalFunctionOperation declaration)
    {
        for (var parent = operation.Parent; parent is not null && !ReferenceEquals(parent, declaration); parent = parent.Parent)
        {
            if (parent is IConditionalOperation or ISwitchOperation or ISwitchExpressionOperation
                or ILoopOperation or ITryOperation or IConditionalAccessOperation
                or IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return true;
            }
        }

        return false;
    }

    // nameof(list) is a compile-time constant: the reference never reads the object at
    // runtime, so it is neither a use nor read-evidence in the RHS/sibling-argument scans.
    private static bool IsInsideNameOf(IOperation operation)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is INameOfOperation)
            {
                return true;
            }
        }

        return false;
    }

    // A local-function declaration or lambda body does not execute (or throw, or exit a loop)
    // on the enclosing path: nothing inside one counts as code the enclosing flow runs.
    private static bool IsWithinANestedFunction(IOperation operation, IOperation boundary)
    {
        for (var current = operation; current is not null && !ReferenceEquals(current, boundary); current = current.Parent)
        {
            if (current is ILocalFunctionOperation or IAnonymousFunctionOperation)
            {
                return true;
            }
        }

        return false;
    }

    // Whether the region can still throw ONCE THE WRAPPER EXISTS: calls, creations, awaits,
    // getters and element reads that END strictly after the transfer -- an op enclosing the
    // transfer (MayThrow(Sending.Transfer(list))) completes after building the wrapper, while
    // the Transfer call itself ends exactly AT the position (a throw from it means no wrapper
    // escaped).
    private static bool CanThrowAfterTheTransfer(IOperation region, int transferPosition)
    {
        return region.DescendantsAndSelf().Any(operation =>
            operation.Syntax.Span.End > transferPosition
            && !IsWithinANestedFunction(operation, region)
            && operation is IInvocationOperation or IObjectCreationOperation or IAwaitOperation
                or IPropertyReferenceOperation or IArrayElementReferenceOperation);
    }

    // The use is in an arm of an if/switch/?. whose SIBLING arm holds the transfer: the arms
    // are mutually exclusive, so no execution path runs the use after the transfer. A use in a
    // branch the transfer merely precedes stays reportable (some path runs it after) -- and so
    // does a use in ANY arm when the transfer sits in the construct's always-executed part
    // (`if (Sending.Transfer(list) != null) { list.Add(1); }`: the condition dominates both
    // arms, so the arm runs after the handoff).
    private static bool IsInASiblingArmOfTheTransfer(IOperation use, int transferPosition)
    {
        for (IOperation child = use; child.Parent is { } parent; child = parent)
        {
            var dominatingPart = parent switch
            {
                IConditionalOperation conditional => conditional.Condition,
                ISwitchOperation @switch => @switch.Value,
                ISwitchExpressionOperation switchExpression => switchExpression.Value,
                IConditionalAccessOperation conditionalAccess => conditionalAccess.Operation,
                _ => null,
            };

            if (dominatingPart is not null
                && Covers(parent, transferPosition)
                && !Covers(child, transferPosition)
                && !Covers(dominatingPart, transferPosition)
                && !IsInACaseGuard(parent, transferPosition))
            {
                return true;
            }
        }

        return false;
    }

    // A `when` guard is arm-SELECTION machinery, not an arm: a failing guard falls through to
    // later cases, so a transfer inside one has already run when a later arm executes --
    // `case 0 when Sending.Transfer(list) is null:` followed by `default: list.Add(1);` is a
    // real use after transfer.
    private static bool IsInACaseGuard(IOperation construct, int transferPosition)
    {
        var guards = construct switch
        {
            ISwitchOperation @switch => @switch.Cases.SelectMany(c => c.Clauses)
                .Select(clause => (clause as IPatternCaseClauseOperation)?.Guard),
            ISwitchExpressionOperation switchExpression => switchExpression.Arms.Select(arm => arm.Guard),
            _ => System.Linq.Enumerable.Empty<IOperation?>(),
        };

        return guards.Any(guard => guard is not null && Covers(guard, transferPosition));
    }

    // transferPosition is the transfer's EXCLUSIVE span end, so an operation whose span ends
    // exactly there (the switch value IS the transfer: `switch (Sending.Transfer(list))`)
    // still covers it -- plain TextSpan.Contains excludes its own end.
    private static bool Covers(IOperation operation, int transferPosition)
    {
        var span = operation.Syntax.Span;
        return span.Contains(transferPosition) || span.End == transferPosition;
    }

    // The region a skipped conditional reinitialization dominates: its nearest enclosing arm
    // (or callback body -- a deferred reinitialization dominates only inside its own callback,
    // where source order applies again).
    private static IOperation DominatingRegion(IOperation reinitialization)
    {
        for (IOperation child = reinitialization; child.Parent is { } parent; child = parent)
        {
            if (parent is IConditionalOperation or ISwitchOperation or ISwitchExpressionOperation
                or ILoopOperation or ITryOperation or IConditionalAccessOperation
                or IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return child;
            }
        }

        return reinitialization;
    }

    // The exception WINDOW between the transfer and a completed reinitialization: when the
    // reinitializing expression can throw (an invocation, creation or await), an enclosing
    // catch entered from the try BODY observes the still-transferred value.
    private static IOperation? CatchUseDuringReinitialization(IOperation reinitialization, ISymbol variable, IOperation scope)
    {
        var reinitRoot = reinitialization is IArgumentOperation { Parent: { } call } ? call : reinitialization;
        var canThrow = reinitRoot.DescendantsAndSelf()
            .Any(operation => operation is IInvocationOperation or IObjectCreationOperation or IAwaitOperation
                or IPropertyReferenceOperation or IArrayElementReferenceOperation);
        if (!canThrow)
        {
            return null;
        }

        for (IOperation child = reinitRoot; child.Parent is { } parent && !ReferenceEquals(child, scope); child = parent)
        {
            if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                break;
            }

            if (parent is ITryOperation tryOperation && ReferenceEquals(child, tryOperation.Body))
            {
                var use = WindowRegionsOf(tryOperation)
                    .SelectMany(region => region.DescendantsAndSelf())
                    .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                        && !IsInsideNameOf(operation));
                if (use is not null)
                {
                    return use;
                }
            }
        }

        return null;
    }

    // The window reaches the handlers AND the finally: both run when the reinitializing
    // expression throws before the target was assigned.
    private static IEnumerable<IOperation> WindowRegionsOf(ITryOperation tryOperation)
    {
        foreach (var catchClause in tryOperation.Catches)
        {
            yield return catchClause;
        }

        if (tryOperation.Finally is { } finallyBlock)
        {
            yield return finallyBlock;
        }
    }

    private enum ReferenceRole { Use, FreshValueFromHere, ConditionalReinitialization }

    // A reference that REINITIALIZES the variable ends tracking when it definitely executes
    // and is skipped when conditional (sibling arms may not run on the path that transferred).
    // Reinitializations are a simple-assignment target -- unless its RHS itself READS the
    // transferred value (`list = Clone(list)` builds the replacement from it: that read is the
    // use to report) -- and an `out` argument, which the callee must assign and cannot read;
    // the out-assignment happens only when the callee RUNS, after every sibling argument was
    // evaluated, so a sibling argument reading the variable is still a use of the transferred
    // value.
    private static ReferenceRole Classify(IOperation reference, ISymbol variable, int transferPosition, IOperation scope, out IOperation? rhsUse)
    {
        rhsUse = null;

        if (reference.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, reference))
        {
            rhsUse = assignment.Value.DescendantsAndSelf()
                .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                    && !IsInsideNameOf(operation));
            return rhsUse is not null ? ReferenceRole.Use : ReinitializationRole(assignment, transferPosition, scope);
        }

        if (reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out } outArgument)
        {
            rhsUse = SiblingArgumentRead(outArgument, variable, transferPosition);
            return rhsUse is not null ? ReferenceRole.Use : ReinitializationRole(outArgument, transferPosition, scope);
        }

        // (list, _) = (...): a deconstruction target is definitely assigned like a
        // simple-assignment target, with the same RHS-read caveat.
        if (DeconstructionOf(reference) is { } deconstruction)
        {
            rhsUse = deconstruction.Value.DescendantsAndSelf()
                .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                    && !IsInsideNameOf(operation));
            return rhsUse is not null ? ReferenceRole.Use : ReinitializationRole(deconstruction, transferPosition, scope);
        }

        return ReferenceRole.Use;
    }

    // The reference must be a tuple ELEMENT on the deconstruction's target side; a read nested
    // inside an element expression (arr[list.Count]) is an ordinary use.
    private static IDeconstructionAssignmentOperation? DeconstructionOf(IOperation reference)
    {
        var current = reference;
        while (current.Parent is ITupleOperation or IConversionOperation)
        {
            current = current.Parent;
        }

        return current.Parent is IDeconstructionAssignmentOperation deconstruction
            && ReferenceEquals(deconstruction.Target, current)
            ? deconstruction
            : null;
    }

    private static IOperation? SiblingArgumentRead(IArgumentOperation outArgument, ISymbol variable, int transferPosition)
    {
        var arguments = outArgument.Parent switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => ImmutableArray<IArgumentOperation>.Empty,
        };

        // Position-filtered: when the same invocation performs the handoff
        // (Reset(Sending.Transfer(list), out list)), the reference inside the transfer
        // argument itself is the handoff, not a post-transfer read.
        return arguments.Where(argument => !ReferenceEquals(argument, outArgument))
            .SelectMany(argument => argument.Value.DescendantsAndSelf())
            .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                && operation.Syntax.SpanStart >= transferPosition
                && !IsInsideNameOf(operation));
    }

    private static ReferenceRole ReinitializationRole(IOperation reinitialization, int transferPosition, IOperation scope)
    {
        // A reinitialization inside a nested callback is DEFERRED: the outer flow cannot count
        // on it having run (the mirror of scoping transfers to their own body). It still
        // dominates within its own callback, where source order applies again.
        if (!ReferenceEquals(EnclosingFunctionBody(reinitialization, scope), scope))
        {
            return ReferenceRole.ConditionalReinitialization;
        }

        return IsOnTheTransfersConditionalLevel(reinitialization, transferPosition)
                && !ABranchCanSkip(reinitialization, transferPosition, scope)
            ? ReferenceRole.FreshValueFromHere
            : ReferenceRole.ConditionalReinitialization;
    }

    // `while (c) { Transfer(list); if (skip) break; list = new(); }`: the break exits the loop
    // PAST the reassignment, so the transferred value survives into the code after the loop. A
    // reinitialization is not definite when a break/continue/goto sits between the transfer
    // and it and jumps out of a construct that also contains it -- a break leaving a switch
    // that closes BEFORE the reinitialization skips nothing.
    private static bool ABranchCanSkip(IOperation reinitialization, int transferPosition, IOperation scope)
    {
        var reinitPosition = reinitialization.Syntax.SpanStart;
        return scope.DescendantsAndSelf().OfType<IBranchOperation>().Any(branch =>
            branch.Syntax.SpanStart >= transferPosition
            && branch.Syntax.Span.End <= reinitPosition
            // A branch in a mutually exclusive sibling arm of the transfer never runs on the
            // path that transferred (`if (move) Transfer(list); else break;`): it cannot skip
            // anything on that path.
            && !IsInASiblingArmOfTheTransfer(branch, transferPosition)
            && CanSkipPast(branch, reinitPosition));
    }

    private static bool CanSkipPast(IBranchOperation branch, int reinitPosition)
    {
        if (branch.BranchKind == BranchKind.GoTo)
        {
            return true; // an arbitrary target is assumed able to skip the reinitialization
        }

        for (var parent = branch.Parent; parent is not null; parent = parent.Parent)
        {
            var exits = branch.BranchKind == BranchKind.Break
                ? parent is ILoopOperation or ISwitchOperation
                : parent is ILoopOperation;
            if (exits)
            {
                return parent.Syntax.Span.Contains(reinitPosition);
            }
        }

        return false;
    }

    // True when the reassignment DEFINITELY executes on the path that transferred: walking up
    // from the assignment, every branching ancestor (if/switch/loop/try/?.) must be entered
    // through the ARM that also contains the transfer. An ancestor merely spanning the
    // transfer is not enough -- `try { Transfer(list); } catch { list = new(); }` spans it,
    // but the catch arm may never run. Checking the path-CHILD's span covers both cases at
    // once (a child containing the transfer implies its parent does too); the one arm exempted
    // is a finally block, which always executes.
    private static bool IsOnTheTransfersConditionalLevel(IOperation reinitialization, int transferPosition)
    {
        for (IOperation child = reinitialization; child.Parent is { } parent; child = parent)
        {
            var branches = parent is IConditionalOperation or ISwitchOperation or ISwitchExpressionOperation
                or ILoopOperation or ITryOperation or IConditionalAccessOperation;
            if (!branches || (parent is ITryOperation tryOperation && ReferenceEquals(child, tryOperation.Finally)))
            {
                continue;
            }

            if (!Covers(child, transferPosition))
            {
                return false;
            }
        }

        return true;
    }

    private static void Report(OperationBlockAnalysisContext context, IOperation reference, ISymbol variable)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.UseAfterTransfer,
            reference.Syntax.GetLocation(),
            variable.Name));
    }

    private static ISymbol? ReferencedVariable(IOperation? operation)
    {
        return operation switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IConversionOperation conversion => ReferencedVariable(conversion.Operand),
            _ => null,
        };
    }

    private static bool IsSendingType(ITypeSymbol? type)
    {
        return type is INamedTypeSymbol named
            && named.OriginalDefinition is { Name: "Sending", Arity: 1 } definition
            && definition.ContainingNamespace?.ToDisplayString() == "MemoizR"
            && FactoryMethods.IsLibraryType(definition);
    }

    private static bool IsSendingHelper(IMethodSymbol method)
    {
        return method.ContainingType is { Name: "Sending", Arity: 0 } type
            && type.ContainingNamespace?.ToDisplayString() == "MemoizR"
            && FactoryMethods.IsLibraryType(type);
    }
}
