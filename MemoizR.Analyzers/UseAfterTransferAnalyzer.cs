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
            foreach (var (variable, transferPosition, scanRoots, escaped, transfer, scope) in Transfers(block))
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
                    if (!ReportUsesAfter(context, new List<IOperation> { enclosingReinit }, escaped, variable, transferPosition, scope)
                        && CatchUseDuringReinitialization(enclosingReinit, variable, scope) is { } windowUse)
                    {
                        Report(context, windowUse, variable);
                    }

                    continue;
                }

                ReportUsesAfter(context, scanRoots, escaped, variable, transferPosition, scope);
            }
        }
    }

    // Every Sending<T> creation (constructor or Sending.Transfer) whose argument is a
    // local/parameter reference, with the position the transfer happens at, the regions the
    // report walk covers, and the enclosing function body -- declarations and delegate
    // stores resolve against the BODY even when the scanned regions are narrower (an
    // escaping `return Pair(Sending.Transfer(list), Use());` still calls a Use declared
    // outside the return expression).
    private static IEnumerable<(ISymbol Variable, int Position, List<IOperation> ScanRoots, bool Escaped, IOperation Transfer, IOperation Scope)> Transfers(IOperation block)
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

                foreach (var entry in EntriesFor(operation, variable, block))
                {
                    yield return entry;
                }
            }
        }
    }

    private static IEnumerable<(ISymbol Variable, int Position, List<IOperation> ScanRoots, bool Escaped, IOperation Transfer, IOperation Scope)> EntriesFor(
        IOperation transfer, ISymbol variable, IOperation block)
    {
        var enclosingBody = EnclosingFunctionBody(transfer, block);
        var (scanRoots, escaped) = ScanRootsFor(transfer, enclosingBody);
        if (scanRoots.Count > 0)
        {
            yield return (variable, transfer.Syntax.Span.End, scanRoots, escaped, transfer, enclosingBody);
        }

        // A transfer inside a CALLED local function is sequenced before the caller's
        // continuation: every call site acts as a transfer of its own. Lambdas stay
        // deferred-scoped (they may run later or never).
        foreach (var propagated in CallSiteTransfers(enclosingBody, variable, block,
            new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default)))
        {
            yield return propagated;
        }
    }

    // Call sites of the local function whose body contains the transfer -- direct calls and
    // DELEGATE-CARRIED ones -- transitively: a call inside ANOTHER declaration only runs
    // through that declaration's own callers. A transferred PARAMETER remaps to the caller's
    // argument at each site.
    private static IEnumerable<(ISymbol Variable, int Position, List<IOperation> ScanRoots, bool Escaped, IOperation Transfer, IOperation Scope)> CallSiteTransfers(
        IOperation transferBody, ISymbol variable, IOperation block, HashSet<IMethodSymbol> visited)
    {
        if (transferBody is not ILocalFunctionOperation declaration || !visited.Add(declaration.Symbol))
        {
            yield break;
        }

        foreach (var invocation in block.DescendantsAndSelf().OfType<IInvocationOperation>())
        {
            if (!InvokesTheDeclaration(invocation, declaration, block))
            {
                continue;
            }

            var callBody = EnclosingFunctionBody(invocation, block);
            if (ReferenceEquals(callBody, transferBody))
            {
                continue; // self-recursion: the body scan already covers it
            }

            foreach (var entry in CallSiteEntries(invocation, callBody, variable, declaration, block, visited))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<(ISymbol Variable, int Position, List<IOperation> ScanRoots, bool Escaped, IOperation Transfer, IOperation Scope)> CallSiteEntries(
        IInvocationOperation invocation, IOperation callBody, ISymbol variable, ILocalFunctionOperation declaration, IOperation block, HashSet<IMethodSymbol> visited)
    {
        foreach (var callVariable in CallSiteVariables(invocation, variable, declaration))
        {
            if (callBody is ILocalFunctionOperation)
            {
                foreach (var nested in CallSiteTransfers(callBody, callVariable, block, visited))
                {
                    yield return nested;
                }

                continue;
            }

            var (roots, escaped) = ScanRootsFor(invocation, callBody);
            if (roots.Count > 0)
            {
                yield return (callVariable, invocation.Syntax.Span.End, roots, escaped, invocation, callBody);
            }
        }
    }

    private static bool InvokesTheDeclaration(IInvocationOperation invocation, ILocalFunctionOperation declaration, IOperation block)
    {
        // move() runs Move when the delegate local can hold it at the call.
        if (invocation.TargetMethod.MethodKind == MethodKind.DelegateInvoke)
        {
            return DelegateReceiver(invocation) is { } receiver
                && StoredCallables(receiver, block, invocation, transferPosition: 0, new HashSet<ISymbol>(SymbolEqualityComparer.Default))
                    .OfType<IMethodReferenceOperation>()
                    .Any(reference => SymbolEqualityComparer.Default.Equals(reference.Method.OriginalDefinition, declaration.Symbol));
        }

        return SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, declaration.Symbol);
    }

    // The caller-side variable the transferred callee variable aliases at THIS call: a
    // captured local stays itself; a PARAMETER maps to the matching argument's sources.
    private static IEnumerable<ISymbol> CallSiteVariables(IInvocationOperation invocation, ISymbol variable, ILocalFunctionOperation declaration)
    {
        if (variable is not IParameterSymbol parameter
            || !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol.OriginalDefinition, declaration.Symbol))
        {
            yield return variable;
            yield break;
        }

        var argument = invocation.Arguments.FirstOrDefault(candidate => candidate.Parameter?.Ordinal == parameter.Ordinal);
        if (argument is null)
        {
            yield break;
        }

        foreach (var source in TransferSources(argument.Value))
        {
            if (ReferencedVariable(source) is { } mapped)
            {
                yield return mapped;
            }
        }
    }

    private static IOperation? EnclosingReinitializingAssignment(IOperation transfer, ISymbol variable)
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

            // (list, _) = (new List<int>(), Sending.Transfer(list)): the deconstruction
            // assigns the fresh element to the variable right after its RHS evaluated.
            if (parent is IDeconstructionAssignmentOperation deconstruction
                && !ReferenceEquals(child, deconstruction.Target)
                && DeconstructionFreshlyResets(deconstruction, variable))
            {
                return deconstruction;
            }
        }

        return null;
    }

    // Only the POSITIONALLY MATCHED RHS element decides: `(list, _) = (list, Transfer(list))`
    // writes the transferred reference back into the variable, resetting nothing. A
    // non-tuple RHS (a method returning a tuple) is opaque -- it could return the same
    // object -- so tracking continues.
    private static bool DeconstructionFreshlyResets(IDeconstructionAssignmentOperation deconstruction, ISymbol variable)
    {
        var value = deconstruction.Value;
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        if (deconstruction.Target is not ITupleOperation targets || value is not ITupleOperation values)
        {
            return false;
        }

        for (var i = 0; i < targets.Elements.Length && i < values.Elements.Length; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(ReferencedVariable(targets.Elements[i]), variable))
            {
                return ReadWithin(values.Elements[i], variable) is null;
            }
        }

        return false;
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
            var (scopeExitTarget, body) = parent switch
            {
                IUsingOperation usingOperation => ((IOperation?)usingOperation.Resources, usingOperation.Body),
                ILockOperation lockOperation => ((IOperation?)lockOperation.LockedValue, lockOperation.Body),
                _ => (null, null),
            };

            // The construct captured the OBJECT at entry: after a definite reassignment the
            // generated Dispose/Monitor.Exit touches the old object, not the one handed off.
            if (scopeExitTarget is not null
                && SymbolEqualityComparer.Default.Equals(ReferencedVariable(scopeExitTarget), variable)
                && !(body is not null && DefinitelyReassignedBefore(body, variable, transfer)))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the variable definitely no longer holds the captured object when the transfer
    // runs: a reassignment that COMPLETES before it, unskippable within the region. The
    // ASSIGNMENT's end decides -- `stream = MakeFrom(Sending.Transfer(stream))` transfers the
    // old object while the reassignment is still in flight.
    private static bool DefinitelyReassignedBefore(IOperation region, ISymbol variable, IOperation transfer)
    {
        var limit = transfer.Syntax.SpanStart;
        return region.DescendantsAndSelf().Any(operation =>
            operation.Parent is ISimpleAssignmentOperation assignment
            && ReferenceEquals(assignment.Target, operation)
            && assignment.Syntax.Span.End <= limit
            && SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
            && !IsConditionalWithin(assignment, region)
            && !ABranchCanSkip(assignment, region.Syntax.SpanStart, region));
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

            // The enumerator iterates the collection captured at LOOP ENTRY: an iteration
            // that definitely reassigns before transferring hands off a different object
            // than the one MoveNext keeps reading.
            if (parent is IForEachLoopOperation foreachLoop
                && SymbolEqualityComparer.Default.Equals(ReferencedVariable(foreachLoop.Collection), variable)
                && !DefinitelyExitsAfter(foreachLoop, transferPosition)
                && !DefinitelyReassignedBefore(foreachLoop.Body, variable, transfer))
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
        if (operation is IReturnOperation { Kind: not OperationKind.YieldReturn })
        {
            return true;
        }

        // A throw absorbed by a catch INSIDE the loop resumes there: only one no handler on
        // the way out can swallow definitely ends the iteration.
        if (operation is IThrowOperation)
        {
            return !MayBeCaughtWithin(operation, loop);
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
    // the container deterministically stores its contents), an object initializer's writes to
    // COMPILER-KNOWN slots (fields and auto-properties; a custom setter may not store what it
    // was handed, so it stays a leaf), and a conversion's operand. Null marks a leaf.
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
            IObjectCreationOperation creation => ObjectCreationCarriedParts(creation),
            // box with { Value = list }: the receiver's CLONE carries the same reference in
            // its slot. The operand itself stays a leaf like a spread's: the original object
            // is not handed off, only its contents are copied into the clone.
            IWithOperation { Initializer: { } withInitializer } => InitializerCarriedParts(withInitializer),
            IConversionOperation conversion => new[] { conversion.Operand },
            // Transfer([list]): matched by SYNTAX -- ICollectionExpressionOperation is not
            // public in the Roslyn this analyzer compiles against, but the children are
            // walkable regardless.
            { } collection when collection.Syntax is Microsoft.CodeAnalysis.CSharp.Syntax.CollectionExpressionSyntax =>
                collection.ChildOperations.SelectMany(child =>
                    child.Syntax is Microsoft.CodeAnalysis.CSharp.Syntax.SpreadElementSyntax
                        ? SpreadCarriedParts(child)
                        : new[] { (IOperation?)child }),
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
                escape = CrossTryTowardScope(roots, tryOperation, child, escape, transfer.Syntax.Span.End);
            }
        }

        if (escape is null)
        {
            roots.Add(scope);
        }

        return (roots, escape is not null);
    }

    // Crossing a try on the way out: the handlers/finally that observe the escape become
    // scan roots, and the escape itself survives -- unless this try's own catches ABSORB
    // its failure path, which then never leaves the method: control resumes right after
    // the try, and the sender-side scan must too (null ends the escape).
    private static IOperation? CrossTryTowardScope(List<IOperation> roots, ITryOperation tryOperation, IOperation child, IOperation escape, int transferPosition)
    {
        // A return whose expression can throw AFTER building the wrapper
        // (return MayThrow(Sending.Transfer(list));) reaches the handlers like a thrown
        // transfer does.
        var reachesHandlers = escape is IThrowOperation
            || CanThrowAfterTheTransfer(escape, transferPosition);
        AddHandlerRoots(roots, tryOperation, child, escapedByThrow: reachesHandlers);

        if (reachesHandlers && ReferenceEquals(child, tryOperation.Body)
            && EscapeIsAbsorbed(tryOperation, escape))
        {
            return null;
        }

        return escape;
    }

    // A THROW is absorbed by an unfiltered catch matching its type. A RETURN's failure path
    // (its expression throwing after the wrapper exists) carries an UNKNOWABLE exception
    // type, so only a catch-everything absorbs it -- and the return may also complete
    // normally, but the post-try code the resume path runs is reachable either way.
    private static bool EscapeIsAbsorbed(ITryOperation tryOperation, IOperation escape)
    {
        if (escape is IThrowOperation thrown)
        {
            return CatchesTheThrow(tryOperation, thrown);
        }

        return tryOperation.Catches.Any(catchClause => catchClause.Filter is null
            && (catchClause.ExceptionType is not { } caughtType || CatchesEverything(caughtType)));
    }

    private static bool CatchesEverything(ITypeSymbol caughtType)
    {
        return caughtType.SpecialType == SpecialType.System_Object
            || caughtType is INamedTypeSymbol { Name: "Exception", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } };
    }

    // Whether the throw DEFINITELY lands in one of the try's handlers: an unfiltered catch
    // whose type the thrown exception inherits (or a bare catch-all). Filters and unrelated
    // types stay escapes -- neutralizing an escape that actually leaves would scan code the
    // transferred path never runs.
    private static bool CatchesTheThrow(ITryOperation tryOperation, IThrowOperation thrown)
    {
        // The thrown operand hides behind the implicit conversion to System.Exception: the
        // CONVERTED type would never match a derived catch.
        var exception = thrown.Exception;
        while (exception is IConversionOperation conversion)
        {
            exception = conversion.Operand;
        }

        if (exception?.Type is not INamedTypeSymbol thrownType)
        {
            return false;
        }

        return tryOperation.Catches.Any(catchClause => catchClause.Filter is null
            && (catchClause.ExceptionType is not { } caughtType
                || SelfAndBases(thrownType).Contains(caughtType, SymbolEqualityComparer.Default)));
    }

    private static System.Collections.Generic.IEnumerable<ITypeSymbol> SelfAndBases(ITypeSymbol type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    // Whether ANY handler between the operation and the boundary could swallow the throw --
    // filters and types unknown, so possibility suffices.
    private static bool MayBeCaughtWithin(IOperation thrown, IOperation boundary)
    {
        for (IOperation child = thrown; child.Parent is { } parent && !ReferenceEquals(child, boundary); child = parent)
        {
            if (parent is ITryOperation tryOperation && ReferenceEquals(child, tryOperation.Body)
                && tryOperation.Catches.Length > 0)
            {
                return true;
            }
        }

        return false;
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

    private static bool ReportUsesAfter(OperationBlockAnalysisContext context, List<IOperation> scanRoots, bool escaped, ISymbol variable, int transferPosition, IOperation scope)
    {

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
            .Select(operation => (Operation: operation, CallEffect: default(BodyEffect?)));

        // A call to a local function (or through a delegate local) whose body reads the
        // variable is a use AT THE CALL: the body's reference sits source-BEFORE the transfer
        // when declared earlier, so the position filter alone would hide it. A body that
        // definitely REWRITES makes the call a reinitialization instead. Calls written inside
        // declarations run only through a call TO the declaration -- the call-site effect
        // resolves the chain transitively, so they are pruned here.
        var callableCalls = scanRoots
            .SelectMany(root => root.DescendantsAndSelf())
            .OfType<IInvocationOperation>()
            .Where(invocation => invocation.TargetMethod.MethodKind is MethodKind.LocalFunction or MethodKind.DelegateInvoke
                // A call WRAPPING the transfer (Use(Sending.Transfer(list))) starts before it,
                // but its body runs only after all arguments -- the handoff included.
                && (invocation.Syntax.SpanStart >= transferPosition || Covers(invocation, transferPosition))
                && !IsInASiblingArmOfTheTransfer(invocation, transferPosition)
                && (escaped || !IsInAnUnreachableCatch(invocation, transferPosition))
                && !IsWithinALocalFunctionBody(invocation, scanRoots))
            .Select(invocation => (Operation: (IOperation)invocation,
                CallEffect: (BodyEffect?)CallEffectOf(invocation, variable, transferPosition, scope)))
            .Where(call => call.CallEffect != BodyEffect.None);

        var orderedUses = laterReferences.Concat(callableCalls)
            .OrderBy(use => use.Operation.Syntax.SpanStart);

        // Regions where a SKIPPED conditional reinitialization dominates: inside its own arm,
        // after it, the variable is fresh on every path that reaches the reference
        // (`if (reset) { list = new(); list.Add(1); }` -- the Add never sees the transferred
        // list), so such references are clean while the scan continues past the arm.
        List<(Microsoft.CodeAnalysis.Text.TextSpan Region, int Position)>? dominated = null;

        foreach (var (reference, callEffect) in orderedUses)
        {
            if (IsDominatedByASkippedReinitialization(dominated, reference))
            {
                continue;
            }

            if (callEffect is { } effect)
            {
                if (LocalFunctionCallOutcome(context, reference, variable, effect, transferPosition, scope, ref dominated) is { } outcome)
                {
                    return outcome;
                }

                continue;
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

    private static BodyEffect CallEffectOf(IInvocationOperation invocation, ISymbol variable, int transferPosition, IOperation scope)
    {
        return invocation.TargetMethod.MethodKind == MethodKind.LocalFunction
            ? LocalFunctionCallEffect(invocation.TargetMethod, variable, scope, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default))
            : DelegateCallEffect(invocation, variable, transferPosition, scope);
    }

    // A reading call is the use itself -- unless its own ARGUMENTS definitely reset the
    // variable first, feeding the body the fresh value. A resetting call (by body or by
    // argument) ends tracking exactly like an inline reinitialization -- when the CALL
    // definitely runs; a conditional one dominates only its own arm -- minus the throw
    // window: the callee can throw into an enclosing catch before its reset landed, and the
    // handler then still observes the transferred value. Null: the scan continues.
    private static bool? LocalFunctionCallOutcome(OperationBlockAnalysisContext context, IOperation call, ISymbol variable, BodyEffect effect,
        int transferPosition, IOperation scope, ref List<(Microsoft.CodeAnalysis.Text.TextSpan Region, int Position)>? dominated)
    {
        var argumentsEffect = BodyEffect.None;
        if (call is IInvocationOperation invocation)
        {
            argumentsEffect = ArgumentsEffect(invocation, variable, transferPosition, out var argumentRead);
            if (argumentRead is not null)
            {
                Report(context, argumentRead, variable);
                return true;
            }
        }

        if (effect == BodyEffect.Reads && argumentsEffect != BodyEffect.Resets)
        {
            Report(context, call, variable);
            return true;
        }

        if (ReinitializationRole(call, transferPosition, scope) != ReferenceRole.FreshValueFromHere)
        {
            (dominated ??= new List<(Microsoft.CodeAnalysis.Text.TextSpan, int)>())
                .Add((DominatingRegion(call).Syntax.Span, call.Syntax.Span.End));
            return null;
        }

        if (CatchUseDuringReinitialization(call, variable, scope) is { } windowUse)
        {
            Report(context, windowUse, variable);
            return true;
        }

        return false;
    }

    // Arguments evaluate BEFORE the callee runs: a read among them is a use no body effect
    // can excuse, and a definite reset among them hands every later evaluation -- the body
    // included -- the fresh value.
    private static BodyEffect ArgumentsEffect(IInvocationOperation invocation, ISymbol variable, int floorPosition, out IOperation? read)
    {
        read = null;
        var references = invocation.Arguments
            .SelectMany(argument => argument.Value.DescendantsAndSelf())
            .Where(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                && operation.Syntax.SpanStart >= floorPosition
                && !IsInsideNameOf(operation))
            .OrderBy(operation => operation.Syntax.SpanStart);

        foreach (var reference in references)
        {
            var reset = ResetShapeOf(reference, variable, invocation, out read);
            if (reset is null)
            {
                read = reference;
                return BodyEffect.Reads;
            }

            if (read is not null)
            {
                return BodyEffect.Reads; // the replacement is built FROM the transferred value
            }

            if (!IsConditionalWithin(reset, invocation))
            {
                return BodyEffect.Resets;
            }
        }

        return BodyEffect.None;
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
    // it: when nothing THROW-CAPABLE follows the handoff in the try body -- later code that
    // cannot throw (`var x = 0;`) never reaches the handler with a completed wrapper, and a
    // throw from the transfer expression itself means no wrapper escaped. (Escaping thrown
    // transfers scan their handlers deliberately: the throw carries the completed wrapper
    // into them.)
    private static bool IsInAnUnreachableCatch(IOperation use, int transferPosition)
    {
        for (IOperation child = use; child.Parent is { } parent; child = parent)
        {
            if (parent is ITryOperation { Body: { } body } && child is ICatchClauseOperation
                && Covers(body, transferPosition)
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

    private enum BodyEffect { None, Reads, Resets }

    // What ENTERING the region does to the variable, scanned in source order from the top.
    // Reads: some path reads the transferred value (`read` is that reference -- possibly the
    // RHS/sibling argument of a reset built FROM it, or a call that reads transitively).
    // Resets: a non-reading reset (simple assignment, out argument, deconstruction target, or
    // a call whose body definitely rewrites) that runs before any read on every path (not
    // nested in a conditional/loop/try, no branch able to skip it) rewrites the variable.
    // None: the region never touches the transferred value -- reads a skipped conditional
    // reset dominates (same arm, after it) see the fresh value, and references inside nested
    // local functions the region never invokes cannot run at all. Calls to sibling local
    // functions resolve against `scope`; `inProgress` breaks recursion cycles.
    private static BodyEffect EffectOf(IOperation region, ISymbol variable, IOperation scope, HashSet<IMethodSymbol> inProgress, out IOperation? read)
    {
        read = null;
        var events = region.DescendantsAndSelf()
            .Where(operation => IsABodyEvent(operation, variable, region))
            .OrderBy(operation => operation.Syntax.SpanStart);

        var dominated = default(List<(Microsoft.CodeAnalysis.Text.TextSpan Region, int Position)>);
        foreach (var current in events)
        {
            if (IsDominatedByASkippedReinitialization(dominated, current))
            {
                continue;
            }

            var (effect, evidence, reset) = ClassifyBodyEvent(current, variable, region, scope, inProgress);
            if (effect == BodyEffect.Reads)
            {
                read = evidence;
                return BodyEffect.Reads;
            }

            if (effect != BodyEffect.Resets)
            {
                continue;
            }

            if (!IsConditionalWithin(reset!, region)
                && !ABranchCanSkip(reset!, region.Syntax.SpanStart, region))
            {
                return BodyEffect.Resets; // rewrites before any read on every path
            }

            (dominated ??= new List<(Microsoft.CodeAnalysis.Text.TextSpan, int)>())
                .Add((DominatingRegion(reset!).Syntax.Span, current.Syntax.Span.End));
        }

        return BodyEffect.None;
    }

    // The operations EffectOf walks: references to the variable, plus LOCAL-FUNCTION and
    // DELEGATE calls, whose bodies read or reset transitively (`void Outer() { Use(); }` /
    // `void F() { use(); }` -- the callee's reference sits source-before the transfer, so
    // only the call chain surfaces it).
    private static bool IsABodyEvent(IOperation operation, ISymbol variable, IOperation region)
    {
        if (operation is IInvocationOperation { TargetMethod.MethodKind: MethodKind.LocalFunction or MethodKind.DelegateInvoke })
        {
            return !IsInAnUnreferencedNestedFunction(operation, region);
        }

        return SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
            && !IsInsideNameOf(operation)
            && !IsInAnUnreferencedNestedFunction(operation, region);
    }

    private static (BodyEffect Effect, IOperation? Evidence, IOperation? Reset) ClassifyBodyEvent(
        IOperation current, ISymbol variable, IOperation region, IOperation scope, HashSet<IMethodSymbol> inProgress)
    {
        if (current is IInvocationOperation invocation)
        {
            // Arguments run BEFORE the body: their reads report and their definite resets
            // feed the body the fresh value.
            var argumentsEffect = ArgumentsEffect(invocation, variable, region.Syntax.SpanStart, out var argumentRead);
            if (argumentRead is not null)
            {
                return (BodyEffect.Reads, argumentRead, null);
            }

            // Delegate stores resolve without the transfer-arm filter here (position 0):
            // which arm holds the transfer is a scope-level question the body cannot ask.
            var callEffect = invocation.TargetMethod.MethodKind == MethodKind.DelegateInvoke
                ? DelegateCallEffect(invocation, variable, transferPosition: 0, scope)
                : LocalFunctionCallEffect(invocation.TargetMethod, variable, scope, inProgress);
            if (argumentsEffect == BodyEffect.Resets)
            {
                return (BodyEffect.Resets, null, invocation);
            }

            return callEffect == BodyEffect.Reads
                ? (BodyEffect.Reads, invocation, null)
                : (callEffect, null, invocation);
        }

        var reset = ResetShapeOf(current, variable, region, out var resetRead);
        if (reset is null)
        {
            return (BodyEffect.Reads, current, null); // a plain read of the transferred value
        }

        return resetRead is not null
            ? (BodyEffect.Reads, resetRead, null) // the replacement is built FROM the transferred value
            : (BodyEffect.Resets, null, reset);
    }

    // The reset operation the reference belongs to (null when it is an ordinary read),
    // mirroring the outer Classify's shapes: a simple-assignment target, an `out` argument
    // (the callee must assign it and cannot read it), or a deconstruction target. `read`
    // carries the reference that still reads the transferred value on the way in -- the
    // reassignment's RHS or a sibling argument.
    private static IOperation? ResetShapeOf(IOperation reference, ISymbol variable, IOperation region, out IOperation? read)
    {
        read = null;

        if (reference.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, reference))
        {
            read = ReadWithin(assignment.Value, variable);
            return assignment;
        }

        if (reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out } outArgument)
        {
            read = SiblingArgumentRead(outArgument, variable, region.Syntax.SpanStart);
            return outArgument;
        }

        if (DeconstructionOf(reference) is { } deconstruction)
        {
            read = ReadWithin(deconstruction.Value, variable);
            return deconstruction;
        }

        return null;
    }

    // Everything an object creation deterministically stores: its initializer's
    // compiler-known slots -- and, for a POSITIONAL RECORD, the primary constructor's
    // arguments, each stored in its same-named auto-property.
    private static System.Collections.Generic.IEnumerable<IOperation?> ObjectCreationCarriedParts(IObjectCreationOperation creation)
    {
        if (creation.Initializer is { } initializer)
        {
            foreach (var part in InitializerCarriedParts(initializer))
            {
                yield return part;
            }
        }

        if (creation.Constructor is not { ContainingType: { IsRecord: true } recordType })
        {
            yield break;
        }

        foreach (var argument in creation.Arguments)
        {
            if (argument.Parameter is { } parameter
                && recordType.GetMembers(parameter.Name).OfType<IPropertySymbol>().Any(SendableSymbolClassifier.HasBackingSlot))
            {
                yield return argument.Value;
            }
        }
    }

    // Member writes carry through compiler-known slots only; an INDEXER initializer
    // (["x"] = list) stores value AND keys into the collection; a collection initializer's
    // Add elements carry like collection expressions.
    private static System.Collections.Generic.IEnumerable<IOperation?> InitializerCarriedParts(IObjectOrCollectionInitializerOperation initializer)
    {
        return initializer.Initializers.SelectMany(element => element switch
        {
            ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true } indexer } member =>
                indexer.Arguments.Select(argument => (IOperation?)argument.Value).Concat(new[] { (IOperation?)member.Value }),
            ISimpleAssignmentOperation member when StoresIntoACompilerKnownSlot(member) => new[] { (IOperation?)member.Value },
            IInvocationOperation add => add.Arguments.Select(argument => (IOperation?)argument.Value),
            _ => System.Linq.Enumerable.Empty<IOperation?>(),
        });
    }

    private static bool StoresIntoACompilerKnownSlot(ISimpleAssignmentOperation member)
    {
        return member.Target is IFieldReferenceOperation
            || (member.Target is IPropertyReferenceOperation property && SendableSymbolClassifier.HasBackingSlot(property.Property));
    }

    // `[..operand]` enumerates the operand INTO the new collection: the operand OBJECT is
    // never carried, but an inline container's elements are -- [.. new[] { list }] delivers
    // the same list reference to the receiver. So only the operand's own carried parts
    // unfold; a plain variable operand contributes nothing.
    private static System.Collections.Generic.IEnumerable<IOperation?> SpreadCarriedParts(IOperation spread)
    {
        var operand = spread.ChildOperations.FirstOrDefault();
        while (operand is IConversionOperation conversion)
        {
            operand = conversion.Operand;
        }

        return operand is not null && CarriedParts(operand) is { } parts
            ? parts
            : System.Linq.Enumerable.Empty<IOperation?>();
    }

    private static IOperation? ReadWithin(IOperation expression, ISymbol variable)
    {
        return expression.DescendantsAndSelf().FirstOrDefault(operation =>
            SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
            && !IsInsideNameOf(operation));
    }

    // A reference inside a nested local function that nothing in the region ever invokes (or
    // lifts into a delegate) can never run by entering the region: the declaration alone
    // executes nothing. Lambda bodies stay visible -- building the delegate is itself the
    // escape.
    private static bool IsInAnUnreferencedNestedFunction(IOperation reference, IOperation region)
    {
        for (var parent = reference.Parent; parent is not null && !ReferenceEquals(parent, region); parent = parent.Parent)
        {
            if (parent is ILocalFunctionOperation nested && !IsReferencedWithin(region, nested.Symbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReferencedWithin(IOperation region, IMethodSymbol localFunction)
    {
        return region.DescendantsAndSelf().Any(operation => operation switch
        {
            IInvocationOperation invocation => SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, localFunction),
            IMethodReferenceOperation methodReference => SymbolEqualityComparer.Default.Equals(methodReference.Method.OriginalDefinition, localFunction),
            _ => false,
        });
    }

    // Whether CALLING the local function reads the transferred value -- or definitely
    // rewrites it, making the call act as a reinitialization at the call site. A recursive
    // chain re-entering a body already on the walk contributes nothing new: mutation is
    // detected at the reference where it occurs.
    private static BodyEffect LocalFunctionCallEffect(IMethodSymbol localFunction, ISymbol variable, IOperation scope, HashSet<IMethodSymbol> inProgress)
    {
        // Use<int>() carries a CONSTRUCTED symbol; the declaration carries the definition.
        var definition = localFunction.OriginalDefinition;
        var declaration = scope.DescendantsAndSelf().OfType<ILocalFunctionOperation>()
            .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(operation.Symbol, definition));
        if (declaration is null || !inProgress.Add(definition))
        {
            return BodyEffect.None;
        }

        try
        {
            return EffectOf(declaration, variable, scope, inProgress, out _);
        }
        finally
        {
            inProgress.Remove(definition);
        }
    }

    // What invoking the delegate held by a LOCAL does: any lambda (or local function lifted
    // into the delegate) that can be stored in it when the call runs and reads the
    // transferred value makes the call a use -- `Action use = () => list.Add(1);` declared
    // before the handoff runs after it. Never a reset: which stored callable actually runs
    // is dynamic.
    private static BodyEffect DelegateCallEffect(IInvocationOperation invocation, ISymbol variable, int transferPosition, IOperation scope)
    {
        if (DelegateReceiver(invocation) is not { } delegateLocal)
        {
            return BodyEffect.None;
        }

        var candidates = StoredCallables(delegateLocal, scope, invocation, transferPosition,
            new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        var reads = candidates.Any(callable => callable switch
        {
            IAnonymousFunctionOperation lambda =>
                EffectOf(lambda, variable, scope, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default), out _) == BodyEffect.Reads,
            IMethodReferenceOperation { Method: { MethodKind: MethodKind.LocalFunction } method } =>
                LocalFunctionCallEffect(method, variable, scope, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default)) == BodyEffect.Reads,
            _ => false,
        });

        return reads ? BodyEffect.Reads : BodyEffect.None;
    }

    // The local the invocation calls through -- behind a ?. receiver too: use?.Invoke() puts
    // a placeholder in Instance, and the real receiver hangs off the enclosing conditional
    // access.
    private static ISymbol? DelegateReceiver(IInvocationOperation invocation)
    {
        var receiver = invocation.Instance;
        if (receiver is IConditionalAccessInstanceOperation)
        {
            for (var parent = invocation.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is IConditionalAccessOperation conditionalAccess)
                {
                    receiver = conditionalAccess.Operation;
                    break;
                }
            }
        }

        return ReferencedVariable(receiver);
    }

    // Every callable stored in the delegate local that can still be its value AT THE CALL:
    // stores in a sibling arm of the transfer never run on the transferred path, a store
    // that only happens after the call cannot be what it invoked (unless a loop carries it
    // back around), a definite `=` overwrite KILLS every earlier target, `-=` never adds
    // one, and a plain delegate-to-delegate copy carries the SOURCE local's candidates.
    private static System.Collections.Generic.IEnumerable<IOperation> StoredCallables(
        ISymbol delegateLocal, IOperation scope, IOperation consumer, int transferPosition, HashSet<ISymbol> visited)
    {
        if (!visited.Add(delegateLocal))
        {
            yield break;
        }

        var stores = DelegateStores(delegateLocal, scope, consumer, transferPosition);

        // A definite overwrite replaces the whole invocation list: candidates begin at the
        // LAST replace that runs on every path to the call.
        var killPoint = stores
            .Where(store => store.Replaces && !IsConditionalWithin(store.Site, scope))
            .Select(store => store.Site.Syntax.SpanStart)
            .DefaultIfEmpty(int.MinValue)
            .Max();

        foreach (var (site, value, _) in stores)
        {
            if (site.Syntax.SpanStart < killPoint)
            {
                continue;
            }

            if (Callable(value) is { } callable)
            {
                yield return callable;
            }
            else if (ReferencedVariable(value) is { } alias)
            {
                // Action use = use2: the copy SNAPSHOTS what the source holds AT THE COPY --
                // stores landing in the source afterwards are not in use's invocation list.
                foreach (var carried in StoredCallables(alias, scope, site, transferPosition, visited))
                {
                    yield return carried;
                }
            }
        }
    }

    private static List<(IOperation Site, IOperation Value, bool Replaces)> DelegateStores(
        ISymbol delegateLocal, IOperation scope, IOperation consumer, int transferPosition)
    {
        var stores = new List<(IOperation Site, IOperation Value, bool Replaces)>();
        foreach (var operation in scope.DescendantsAndSelf())
        {
            (IOperation Value, bool Replaces)? matched = operation switch
            {
                IVariableDeclaratorOperation declarator
                    when SymbolEqualityComparer.Default.Equals(declarator.Symbol, delegateLocal)
                        && declarator.Initializer is { } initializer
                    => (initializer.Value, true),
                ISimpleAssignmentOperation assignment
                    when SymbolEqualityComparer.Default.Equals(ReferencedVariable(assignment.Target), delegateLocal)
                    => (assignment.Value, true),
                // use += adds a target; -= only removes, storing nothing.
                ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add } combined
                    when SymbolEqualityComparer.Default.Equals(ReferencedVariable(combined.Target), delegateLocal)
                    => (combined.Value, false),
                _ => null,
            };

            if (matched is { } store
                && !IsInASiblingArmOfTheTransfer(operation, transferPosition)
                && (operation.Syntax.SpanStart < consumer.Syntax.Span.End || SharesALoopWith(operation, consumer))
                // A store inside a local function nothing ever invokes never executed.
                && !IsInAnUnreferencedNestedFunction(operation, scope))
            {
                stores.Add((operation, store.Value, store.Replaces));
            }
        }

        return stores;
    }

    private static bool SharesALoopWith(IOperation store, IOperation consumer)
    {
        for (var parent = consumer.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is ILoopOperation && parent.Syntax.Span.Contains(store.Syntax.SpanStart))
            {
                return true;
            }
        }

        return false;
    }

    private static IOperation? Callable(IOperation? stored)
    {
        while (true)
        {
            switch (stored)
            {
                case IDelegateCreationOperation creation:
                    stored = creation.Target;
                    break;
                case IConversionOperation conversion:
                    stored = conversion.Operand;
                    break;
                case IAnonymousFunctionOperation or IMethodReferenceOperation:
                    return stored;
                default:
                    return null;
            }
        }
    }

    // Whether control can reach past the operation without executing it, judged within the
    // region entered at its top: any conditional/loop/switch ancestor below the region root
    // makes it skippable, a nested function's body is deferred entirely, and a try counts
    // only where a path can resume past a failure (catch regions, bodies WITH catches).
    private static bool IsConditionalWithin(IOperation operation, IOperation region)
    {
        for (IOperation child = operation; child.Parent is { } parent && !ReferenceEquals(parent, region); child = parent)
        {
            if (parent is ITryOperation tryOperation)
            {
                if (!RunsToCompletionOrExits(tryOperation, child))
                {
                    return true;
                }

                continue;
            }

            if (parent is IConditionalOperation or ISwitchOperation or ISwitchExpressionOperation
                or ILoopOperation or IConditionalAccessOperation
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
            && CanThrow(operation));
    }

    // The heuristic throw model shared by catch reachability and the reinitialization
    // windows: calls, allocations, awaits and member/element accesses that dereference --
    // a FIELD read only through a possibly-null receiver (`this` cannot be null) -- plus
    // user-defined conversions/operators and explicit reference downcasts.
    private static bool CanThrow(IOperation operation)
    {
        return operation switch
        {
            IFieldReferenceOperation field => field.Instance is not null and not IInstanceReferenceOperation,
            IConversionOperation conversion => conversion.OperatorMethod is not null
                || (conversion.Conversion is { IsReference: true, IsImplicit: false } && !conversion.IsTryCast),
            IUnaryOperation unary => unary.OperatorMethod is not null,
            IBinaryOperation binary => binary.OperatorMethod is not null,
            _ => operation is IInvocationOperation or IObjectCreationOperation or IAwaitOperation
                or IPropertyReferenceOperation or IArrayElementReferenceOperation or IThrowOperation,
        };
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
        var canThrow = reinitRoot.DescendantsAndSelf().Any(CanThrow);
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

            if (parent is ITryOperation tryOperation && ReferenceEquals(child, tryOperation.Body)
                && WindowUseIn(tryOperation, variable, scope) is { } use)
            {
                return use;
            }
        }

        return null;
    }

    // The first window region that actually READS the transferred value. A handler that
    // REWRITES the variable before touching it (catch { list = new(); ... }) is a recovery,
    // not a use -- the reinitialization classification applies inside the window too.
    private static IOperation? WindowUseIn(ITryOperation tryOperation, ISymbol variable, IOperation scope)
    {
        foreach (var region in WindowRegionsOf(tryOperation))
        {
            if (EffectOf(region, variable, scope, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default), out var read) == BodyEffect.Reads)
            {
                return read;
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

        return SiblingArgumentRead(arguments.Where(argument => !ReferenceEquals(argument, outArgument)), variable, transferPosition);
    }

    // Position-filtered: when the same invocation performs the handoff
    // (Reset(Sending.Transfer(list), out list)), the reference inside the transfer
    // argument itself is the handoff, not a post-transfer read.
    private static IOperation? SiblingArgumentRead(System.Collections.Generic.IEnumerable<IArgumentOperation> arguments, ISymbol variable, int transferPosition)
    {
        return arguments
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
    // reinitialization is not definite when a break/continue/goto -- or a CAUGHT throw, whose
    // handler resumes after its try -- sits between the transfer and it and jumps out of a
    // construct that also contains it; a break leaving a switch that closes BEFORE the
    // reinitialization skips nothing.
    private static bool ABranchCanSkip(IOperation reinitialization, int transferPosition, IOperation scope)
    {
        var reinitPosition = reinitialization.Syntax.SpanStart;
        return scope.DescendantsAndSelf().Any(operation =>
            operation.Syntax.SpanStart >= transferPosition
            && operation.Syntax.Span.End <= reinitPosition
            // A jump in a mutually exclusive sibling arm of the transfer never runs on the
            // path that transferred (`if (move) Transfer(list); else break;`): it cannot skip
            // anything on that path.
            && !IsInASiblingArmOfTheTransfer(operation, transferPosition)
            && CanSkipPast(operation, reinitPosition, scope));
    }

    private static bool CanSkipPast(IOperation operation, int reinitPosition, IOperation scope)
    {
        // A caught FAILURE resumes AFTER its try: any throw-capable operation (an explicit
        // throw, a call that may fail, ...) skips a reset that try still contains.
        if (CanThrow(operation))
        {
            return NearestCatchingTry(operation) is { } catchingTry
                && catchingTry.Syntax.Span.Contains(reinitPosition);
        }

        // Returning exits the enclosing function past the reset. Inside a CALLED body's scan
        // that means the call completes without rewriting -- the caller's later uses still
        // see the transferred value. In the OUTER scan those later uses are unreachable on
        // the returning path, so nothing is skipped for them.
        if (operation is IReturnOperation { Kind: not OperationKind.YieldReturn })
        {
            return scope is ILocalFunctionOperation or IAnonymousFunctionOperation;
        }

        if (operation is not IBranchOperation branch)
        {
            return false;
        }

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

    private static ITryOperation? NearestCatchingTry(IOperation thrown)
    {
        for (IOperation child = thrown; child.Parent is { } parent; child = parent)
        {
            if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return null; // a deferred body's throw does not run on this path
            }

            if (parent is ITryOperation tryOperation && ReferenceEquals(child, tryOperation.Body)
                && tryOperation.Catches.Length > 0)
            {
                return tryOperation;
            }
        }

        return null;
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
            if (!branches || (parent is ITryOperation tryOperation && RunsToCompletionOrExits(tryOperation, child)))
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

    // A finally always runs, and a CATCHLESS try body either completes or leaves the scope
    // entirely -- no path resumes past a failed reset inside them, so neither makes the reset
    // conditional. A try body with catches CAN be resumed past (the handler swallows the
    // failure), and a catch region itself runs only on exception.
    private static bool RunsToCompletionOrExits(ITryOperation tryOperation, IOperation child)
    {
        return ReferenceEquals(child, tryOperation.Finally)
            || (ReferenceEquals(child, tryOperation.Body) && tryOperation.Catches.IsEmpty);
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
