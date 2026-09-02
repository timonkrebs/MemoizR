using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

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
        // The classifier caches verdicts per type symbol, so it is scoped to one compilation.
        context.RegisterCompilationStartAction(compilationStart =>
        {
            // No Sending<T> in the compilation, no transfer to find: the block walk is
            // skipped wholesale.
            if (!compilationStart.Compilation.GetTypesByMetadataName("MemoizR.Sending`1").Any(FactoryMethods.IsLibraryType))
            {
                return;
            }

            var classifier = new SendableSymbolClassifier();
            compilationStart.RegisterOperationBlockAction(blockContext => AnalyzeBlock(blockContext, classifier));
        });
    }

    private static void AnalyzeBlock(OperationBlockAnalysisContext context, SendableSymbolClassifier classifier)
    {
        foreach (var block in context.OperationBlocks)
        {
            foreach (var entry in Transfers(block, classifier))
            {
                if (IsUsedByAnEnclosingConstruct(entry))
                {
                    Report(context, entry.Transfer, entry.Variable);
                }
                else if (EnclosingReinitializingAssignment(entry.Transfer, entry.Variable) is { } enclosingReinit)
                {
                    ReportUsesAroundTheEnclosingReinitialization(context, entry, enclosingReinit);
                }
                else
                {
                    ReportUsesAfter(context, entry, entry.ScanRoots);
                }
            }
        }
    }

    // A using-declared local -- or an existing local/parameter handed to a using STATEMENT
    // as its resource -- is Disposed by the SENDER at scope end, after the handoff, with no
    // source reference to scan for: destroying the object the receiver now owns is a
    // guaranteed use-after-transfer. An enclosing foreach keeps iterating it likewise.
    private static bool IsUsedByAnEnclosingConstruct(TransferEntry entry)
    {
        return entry.Variable is ILocalSymbol { IsUsing: true }
            || IsDisposedByAnEnclosingUsing(entry.Transfer, entry.Variable)
            || IsIteratedByAnEnclosingForeach(entry.Transfer, entry.Position, entry.Variable);
    }

    // list = MakeFresh(Sending.Transfer(list)): the assignment ENCLOSING the transfer
    // completes right after the RHS, reinitializing the variable -- only reads still inside
    // the RHS (after the transfer) and the throw window count. The RHS can also throw into
    // a RESUMING catch before the assignment completes: after the try the variable may still
    // hold the transferred value, so later uses stay reportable.
    private static void ReportUsesAroundTheEnclosingReinitialization(OperationBlockAnalysisContext context, TransferEntry entry, IOperation enclosingReinit)
    {
        if (ReportUsesAfter(context, entry, new List<IOperation> { enclosingReinit }))
        {
            return;
        }

        if (CatchUseDuringReinitialization(enclosingReinit, entry.Variable, entry.Position, entry.Scope) is { } windowUse)
        {
            Report(context, windowUse, entry.Variable);
            return;
        }

        if (CanThrowAfterTheTransfer(enclosingReinit, entry.Position)
            && ACaughtFailureCanResumePast(enclosingReinit, enclosingReinit.Syntax.Span.End))
        {
            ReportUsesAfter(context, entry, entry.ScanRoots);
        }
    }

    // A handoff to scan: the transferred variable, the transfer operation (its span end is
    // the position the handoff completes at), the regions the report walk covers, whether
    // the flow leaves them right away, and the enclosing function body -- declarations and
    // delegate stores resolve against the BODY even when the scanned regions are narrower
    // (an escaping `return Pair(Sending.Transfer(list), Use());` still calls a Use declared
    // outside the return expression).
    private sealed class TransferEntry
    {
        public TransferEntry(ISymbol variable, IOperation transfer, List<IOperation> scanRoots, bool escaped, IOperation scope)
        {
            Variable = variable;
            Transfer = transfer;
            ScanRoots = scanRoots;
            Escaped = escaped;
            Scope = scope;
        }

        public ISymbol Variable { get; }

        public IOperation Transfer { get; }

        public List<IOperation> ScanRoots { get; }

        public bool Escaped { get; }

        public IOperation Scope { get; }

        public int Position => Transfer.Syntax.Span.End;
    }

    // Every Sending<T> creation (constructor or Sending.Transfer) whose argument hands off
    // a tracked local/parameter.
    private static IEnumerable<TransferEntry> Transfers(IOperation block, SendableSymbolClassifier classifier)
    {
        foreach (var operation in block.DescendantsAndSelf())
        {
            var argument = operation switch
            {
                IObjectCreationOperation creation when IsSendingType(creation.Type) =>
                    creation.Arguments.FirstOrDefault()?.Value,
                IInvocationOperation { TargetMethod.Name: "Transfer" } invocation when IsSendingHelper(invocation.TargetMethod) =>
                    invocation.Arguments.FirstOrDefault()?.Value,
                _ => null,
            };

            foreach (var source in TransferSources(argument))
            {
                if (TrackedVariableOf(source, classifier) is not { } variable)
                {
                    continue;
                }

                foreach (var entry in EntriesFor(operation, variable, block, classifier))
                {
                    yield return entry;
                }
            }
        }
    }

    // The variable a source hands off -- none when it is not a local/parameter, and none
    // when its type is deeply Sendable: such a source (an int tuple element, say) cannot
    // alias mutable state with the receiver, and tracking it would bury the real handoffs
    // beside it.
    private static ISymbol? TrackedVariableOf(IOperation source, SendableSymbolClassifier classifier)
    {
        if (ReferencedVariable(source) is not { } variable)
        {
            return null;
        }

        // A TYPE PARAMETER passes the classifier by design (creation sites check the closed
        // type), but no closed check exists for a generic transfer helper: track it unless
        // its constraints prove it harmless.
        if (source.Type is ITypeParameterSymbol typeParameter)
        {
            return SendableSymbolClassifier.IsProvenSendableByConstraints(typeParameter) ? null : variable;
        }

        // Sendable by DECLARATION alone is not enough: a non-sealed class -- or a container
        // holding one -- can carry a mutable subclass the receiver then owns (the smuggle
        // hole MZR006 hints at), and a transfer of such a value is exactly where the
        // declared-type verdict cannot be trusted.
        return source.Type is { } sourceType
            && classifier.GetNotSendableReason(sourceType) is null
            && !CanHideAMutableImplementation(sourceType)
            ? null
            : variable;
    }

    private static bool CanHideAMutableImplementation(ITypeSymbol type)
    {
        return SubclassSmugglingAnalyzer.NamedTypesIn(type, depth: 0)
            .Any(entry => SubclassSmugglingAnalyzer.IsSmuggleSurface(entry.Named));
    }

    private static IEnumerable<TransferEntry> EntriesFor(
        IOperation transfer, ISymbol variable, IOperation block, SendableSymbolClassifier classifier)
    {
        var enclosingBody = EnclosingFunctionBody(transfer, block);
        var (scanRoots, escaped) = ScanRootsFor(transfer, enclosingBody);
        if (scanRoots.Count > 0)
        {
            yield return new TransferEntry(variable, transfer, scanRoots, escaped, enclosingBody);
        }

        // A transfer inside a CALLED local function or lambda is sequenced before the
        // caller's continuation: every call site acts as a transfer of its own. A stored
        // but never-invoked callable stays deferred-scoped.
        foreach (var propagated in CallSiteTransfers(enclosingBody, variable, transfer, block, classifier,
            NewSymbolSet<IMethodSymbol>()))
        {
            yield return propagated;
        }
    }

    // Call sites of the local function whose body contains the transfer -- direct calls and
    // DELEGATE-CARRIED ones -- transitively: a call inside ANOTHER declaration only runs
    // through that declaration's own callers. A transferred PARAMETER remaps to the caller's
    // argument at each site.
    private static IEnumerable<TransferEntry> CallSiteTransfers(
        IOperation transferBody, ISymbol variable, IOperation anchor, IOperation block, SendableSymbolClassifier classifier, HashSet<IMethodSymbol> visited)
    {
        var functionSymbol = transferBody switch
        {
            ILocalFunctionOperation declaration => declaration.Symbol,
            IAnonymousFunctionOperation lambda => lambda.Symbol,
            _ => null,
        };
        if (functionSymbol is null || !visited.Add(functionSymbol))
        {
            yield break;
        }

        // A by-value parameter definitely reassigned before the handoff no longer aliases
        // ANY caller argument: the callee transferred its own fresh value.
        // (ref/out parameters write THROUGH to the caller's slot: the alias survives any
        // reassignment, so only by-value parameters lose it.)
        if (variable is IParameterSymbol { RefKind: RefKind.None } parameterVariable
            && SymbolEqualityComparer.Default.Equals(parameterVariable.ContainingSymbol.OriginalDefinition, functionSymbol)
            && DefinitelyReassignedBefore(transferBody, variable, anchor))
        {
            yield break;
        }

        // A callee that never completes normally after its handoff turns each call into a
        // THROWN transfer: only the caller's handlers and finallys observe it.
        var calleeExits = CalleeCannotReturnAfter(transferBody, anchor.Syntax.Span.End);

        // An ITERATOR body runs only when enumerated: the call itself hands nothing off (the
        // sequence may never be walked), so only an immediate enumeration propagates.
        var deferred = IsIteratorBody(transferBody);

        foreach (var invocation in block.DescendantsAndSelf().OfType<IInvocationOperation>())
        {
            if (!IsAPropagatingCallSite(invocation, transferBody, functionSymbol, anchor, block, deferred))
            {
                continue;
            }

            var callBody = EnclosingFunctionBody(invocation, block);
            foreach (var entry in CallSiteEntries(invocation, callBody, variable, functionSymbol, block, classifier, visited, calleeExits))
            {
                yield return entry;
            }
        }
    }

    // A call site the transfer propagates to: it runs the body (an enumeration, for an
    // iterator), it is not in a mutually exclusive sibling arm of the store/transfer -- no
    // path runs it after storing the transferring callable -- and it is not the body's own
    // recursion, which the body scan already covers.
    private static bool IsAPropagatingCallSite(IInvocationOperation invocation, IOperation transferBody, IMethodSymbol functionSymbol, IOperation anchor, IOperation block, bool deferred)
    {
        return InvokesTheBody(invocation, transferBody, functionSymbol, anchor, block)
            && (!deferred || IsEnumeratedImmediately(invocation))
            && !IsInASiblingArmOfTheTransfer(invocation, anchor.Syntax.Span.End)
            && !ReferenceEquals(EnclosingFunctionBody(invocation, block), transferBody);
    }

    private static bool IsIteratorBody(IOperation body)
    {
        return body.DescendantsAndSelf().Any(operation =>
            operation is IReturnOperation { Kind: OperationKind.YieldReturn or OperationKind.YieldBreak }
            && !IsWithinANestedFunction(operation, body));
    }

    // The call's sequence is walked right away: it is a foreach's collection, or the
    // receiver or an argument of a framework method (ToList(), First(), Count(), ...) --
    // every framework consumer of a sequence enumerates it. Stored, returned or handed to
    // user code, the sequence stays deferred.
    private static bool IsEnumeratedImmediately(IInvocationOperation call)
    {
        var consumer = call.Parent;
        while (consumer is IConversionOperation)
        {
            consumer = consumer.Parent;
        }

        return consumer switch
        {
            IForEachLoopOperation => true,
            IInvocationOperation framework => IsFrameworkDeclared(framework.TargetMethod.ContainingType),
            IArgumentOperation { Parent: IInvocationOperation framework } => IsFrameworkDeclared(framework.TargetMethod.ContainingType),
            _ => false,
        };
    }

    // Whether the body never completes normally once the handoff ran: an uncaught throw on
    // the transfer's own conditional level, with no return/break/continue/goto able to leave
    // before it. Judged one level deep -- a caller of such a callee is read from its own body.
    private static bool CalleeCannotReturnAfter(IOperation body, int transferPosition)
    {
        var later = body.DescendantsAndSelf()
            .Where(operation => operation.Syntax.SpanStart >= transferPosition && !IsWithinANestedFunction(operation, body))
            .ToList();
        var exit = later.FirstOrDefault(operation => operation is IThrowOperation
            && IsOnTheTransfersConditionalLevel(operation, transferPosition)
            && !MayBeCaughtWithin(operation, body));
        return exit is not null
            && !later.Any(operation => operation.Syntax.SpanStart < exit.Syntax.SpanStart
                && operation is IReturnOperation or IBranchOperation);
    }

    // The regions a thrown call of unknowable exception type is observable from: the
    // handlers and finallys of the enclosing tries -- and, when a catch-everything resumes,
    // the continuation after that try (the call is then an ordinary transfer again).
    private static (List<IOperation> Roots, bool Escaped) ThrowingCallRoots(IInvocationOperation invocation, IOperation scope)
    {
        var roots = new List<IOperation>();
        for (IOperation child = invocation; child.Parent is { } parent && !ReferenceEquals(parent, scope); child = parent)
        {
            if (IsNestedFunction(parent))
            {
                break;
            }

            if (parent is ITryOperation tryOperation)
            {
                AddHandlerRoots(roots, tryOperation, child, escapedByThrow: true);
                if (ReferenceEquals(child, tryOperation.Body) && HasAbsorbingCatch(tryOperation, CatchesEverything))
                {
                    roots.Add(scope);
                    return (roots, false);
                }
            }
        }

        return (roots, true);
    }

    private static IEnumerable<TransferEntry> CallSiteEntries(
        IInvocationOperation invocation, IOperation callBody, ISymbol variable, IMethodSymbol functionSymbol, IOperation block, SendableSymbolClassifier classifier, HashSet<IMethodSymbol> visited, bool calleeExits)
    {
        foreach (var callVariable in CallSiteVariables(invocation, variable, functionSymbol, classifier))
        {
            if (callBody is ILocalFunctionOperation)
            {
                foreach (var nested in CallSiteTransfers(callBody, callVariable, invocation, block, classifier, visited))
                {
                    yield return nested;
                }

                continue;
            }

            var (roots, escaped) = calleeExits ? ThrowingCallRoots(invocation, callBody) : ScanRootsFor(invocation, callBody);
            if (roots.Count > 0)
            {
                yield return new TransferEntry(callVariable, invocation, roots, escaped, callBody);
            }
        }
    }

    private static bool InvokesTheBody(IInvocationOperation invocation, IOperation transferBody, IMethodSymbol functionSymbol, IOperation anchor, IOperation block)
    {
        // move() runs the body when the delegate local can hold it at the call -- a stored
        // METHOD GROUP matches by symbol, a stored LAMBDA by the operation itself. Stores
        // resolve against the real anchor so sibling-arm stores stay excluded.
        if (invocation.TargetMethod.MethodKind == MethodKind.DelegateInvoke)
        {
            return DelegateReceiver(invocation) is { } receiver
                && StoredCallables(receiver, block, invocation, anchor.Syntax.Span.End, NewSymbolSet<ISymbol>())
                    .Any(callable => ReferenceEquals(callable, transferBody)
                        || (callable is IMethodReferenceOperation methodReference
                            && SymbolEqualityComparer.Default.Equals(methodReference.Method.OriginalDefinition, functionSymbol)));
        }

        return SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, functionSymbol);
    }

    // The caller-side variable the transferred callee variable aliases at THIS call: a
    // captured local stays itself; a PARAMETER maps to the matching argument's sources.
    private static IEnumerable<ISymbol> CallSiteVariables(IInvocationOperation invocation, ISymbol variable, IMethodSymbol functionSymbol, SendableSymbolClassifier classifier)
    {
        if (variable is not IParameterSymbol parameter
            || !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol.OriginalDefinition, functionSymbol))
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
            // The same Sendable filter as the direct path: a boxed int argument hands the
            // receiver nothing mutable.
            if (TrackedVariableOf(source, classifier) is { } mapped)
            {
                yield return mapped;
            }
        }
    }

    private static IOperation? EnclosingReinitializingAssignment(IOperation transfer, ISymbol variable)
    {
        for (IOperation child = transfer; child.Parent is { } parent; child = parent)
        {
            if (IsNestedFunction(parent))
            {
                break;
            }

            if (parent is ISimpleAssignmentOperation assignment
                && !ReferenceEquals(child, assignment.Target)
                && IsReferenceTo(assignment.Target, variable))
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
        var value = PeelConversions(deconstruction.Value);
        if (deconstruction.Target is not ITupleOperation targets || value is not ITupleOperation values)
        {
            return false;
        }

        for (var i = 0; i < targets.Elements.Length && i < values.Elements.Length; i++)
        {
            if (IsReferenceTo(targets.Elements[i], variable))
            {
                return ReadWithin(values.Elements[i], variable) is null;
            }
        }

        return false;
    }

    private static bool IsDisposedByAnEnclosingUsing(IOperation transfer, ISymbol variable)
    {
        // A using outside the callback disposes on the OUTER flow's schedule.
        foreach (var parent in AncestorsWithinFunction(transfer))
        {
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
                && IsReferenceTo(scopeExitTarget, variable)
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
        return region.DescendantsAndSelf()
            .Where(operation => IsReferenceTo(operation, variable))
            .Any(reference => ResetShapeOf(reference, variable, region.Syntax.SpanStart, out var read) is { } reset
                // A replacement built FROM the variable (x = x, x = Wrap(x)) is not provably
                // a different object: the alias may survive.
                && read is null
                && reset.Syntax.Span.End <= limit
                && !IsConditionalWithin(reset, region)
                && !ABranchCanSkip(reset, region.Syntax.SpanStart, region));
    }

    // An enclosing foreach KEEPS READING its collection after the body iteration that
    // performed the handoff: the next MoveNext is a sender-side use with no source reference
    // -- unless the transfer's continuation definitely leaves the loop (a break/return/throw
    // on the transfer's own conditional level).
    private static bool IsIteratedByAnEnclosingForeach(IOperation transfer, int transferPosition, ISymbol variable)
    {
        foreach (var parent in AncestorsWithinFunction(transfer))
        {
            // The enumerator iterates the collection captured at LOOP ENTRY: an iteration
            // that definitely reassigns before transferring hands off a different object
            // than the one MoveNext keeps reading. A collection EXPRESSION built from the
            // variable (list.Where(...)) keeps pulling from the same list -- and even an
            // eager copy re-runs the transfer itself on the next iteration.
            if (parent is IForEachLoopOperation foreachLoop
                && ReadWithin(foreachLoop.Collection, variable) is not null
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

        return Ancestors(branch).FirstOrDefault(parent => parent is ILoopOperation or ISwitchOperation) is { } exited
            && ReferenceEquals(exited, loop);
    }

    // The variables an argument expression can HAND OFF. An assignment transfers its target
    // (Transfer(list = new(...)) / Transfer(list ??= new(...)): the variable aliases the value
    // afterwards); a null-coalescing or conditional expression transfers whichever operand the
    // runtime picks (Transfer(list ?? fallback), Transfer(c ? a : b)) -- each is a
    // MAY-transfer, so each is tracked.
    private static IEnumerable<IOperation> TransferSources(IOperation? argument)
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
    private static IEnumerable<IOperation?>? CarriedParts(IOperation argument)
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
            // box with { Value = list }: the receiver's CLONE copies the operand's stored
            // contents, then applies the initializer -- an INLINE operand's carried parts
            // are in the clone; a variable operand stays a leaf (its object is not handed
            // off, only copied from).
            IWithOperation withOperation => WithCarriedParts(withOperation),
            // A built-in conversion (upcast, boxing) keeps the alias; a USER-DEFINED one
            // manufactures a new value the operand never becomes -- a leaf the sender still
            // owns exclusively.
            IConversionOperation { OperatorMethod: null } conversion => new[] { conversion.Operand },
            // Tuple.Create(list) is the constructor spelling of a framework value carrier,
            // and the immutable/frozen collection factories' Create store their ARGUMENTS
            // as elements the same way (ImmutableArray.Create(list) retains the list).
            // An EXISTING array handed to the params overload is copied element by element
            // (the array object is not retained); the compiler-built param array of the
            // expanded form carries its elements like any inline container.
            IInvocationOperation { TargetMethod: { Name: "Create", ContainingType: { } factory } } factoryCall
                when IsACarrierFactory(factory) =>
                factoryCall.Arguments.SelectMany(argument =>
                    argument is { ArgumentKind: ArgumentKind.Explicit, Parameter.IsParams: true }
                        ? CopiedElementParts(argument.Value)
                        : new[] { (IOperation?)argument.Value }),
            // CreateRange, ToImmutable*/ToFrozen* and LINQ's ToArray/ToList/ToHashSet copy the
            // ELEMENTS out of their source: the source object is never retained (a variable
            // contributes nothing), but an inline container's elements reach the receiver --
            // the same spread semantics as [.. new[] { list }].
            IInvocationOperation { TargetMethod: { ContainingType: { } copier } method } copyCall
                when IsACopyingFactory(method, copier) =>
                copyCall.Arguments.SelectMany(argument => CopiedElementParts(argument.Value)),
            // A framework VIEW (list.AsReadOnly(), Where(...), AsEnumerable(), array.AsMemory())
            // reads through its source on the receiver's schedule -- nothing was copied out --
            // so the receiver and every retained argument ride along.
            IInvocationOperation { TargetMethod: { ContainingType: { } viewSource } viewMethod } view
                when IsAFrameworkView(viewMethod, viewSource) =>
                new[] { (IOperation?)view.Instance }.Concat(view.Arguments.Select(argument => (IOperation?)argument.Value)),
            // Transfer([list]): matched by SYNTAX -- ICollectionExpressionOperation is not
            // public in the Roslyn this analyzer compiles against, but the children are
            // walkable regardless.
            // list?.AsReadOnly(): the null-conditional wrapper carries whatever its inner
            // expression carries, the receiver placeholder standing for the receiver.
            IConditionalAccessOperation conditionalAccess when CarriedParts(conditionalAccess.WhenNotNull) is { } innerParts =>
                innerParts.Select(part => part is IConditionalAccessInstanceOperation ? conditionalAccess.Operation : part),
            { } collection when collection.Syntax is CollectionExpressionSyntax =>
                collection.ChildOperations.SelectMany(child =>
                    child.Syntax is SpreadElementSyntax
                        ? CopiedElementParts(child.ChildOperations.FirstOrDefault())
                        : new[] { (IOperation?)child }),
            _ => null,
        };
    }

    // Framework factories whose Create stores its arguments: the tuple family carries each
    // argument in a field, and the immutable/frozen collection factories build a container
    // around the argument REFERENCES -- freezing the shape shares the contents.
    private static bool IsACarrierFactory(INamedTypeSymbol factory)
    {
        return (factory.Name is "Tuple" or "ValueTuple" or "KeyValuePair"
                || factory.Name.StartsWith("Immutable", StringComparison.Ordinal)
                || factory.Name.StartsWith("Frozen", StringComparison.Ordinal))
            && IsFrameworkDeclared(factory);
    }

    // Framework copiers enumerate a source into a fresh collection: the immutable/frozen
    // CreateRange and ToImmutable*/ToFrozen* conversions, and LINQ's materializers.
    private static bool IsACopyingFactory(IMethodSymbol method, INamedTypeSymbol copier)
    {
        if (!IsFrameworkDeclared(copier))
        {
            return false;
        }

        var immutableFactory = copier.Name.StartsWith("Immutable", StringComparison.Ordinal)
            || copier.Name.StartsWith("Frozen", StringComparison.Ordinal);
        return (method.Name == "CreateRange" && immutableFactory)
            || method.Name.StartsWith("ToImmutable", StringComparison.Ordinal)
            || method.Name.StartsWith("ToFrozen", StringComparison.Ordinal)
            || (copier.Name == "Enumerable" && method.Name is "ToArray" or "ToList" or "ToHashSet" or "ToDictionary" or "ToLookup");
    }

    // A framework method whose result is a live view over what it was handed: it returns an
    // interface (IEnumerable<T>, IReadOnlyList<T>, IEnumerator<T>, ...) or a known view type
    // rather than a collection of its own -- the materializers above are checked first.
    private static bool IsAFrameworkView(IMethodSymbol method, INamedTypeSymbol source)
    {
        return IsFrameworkDeclared(source)
            && method.ReturnType is INamedTypeSymbol returned
            && (returned.TypeKind == TypeKind.Interface
                || returned.OriginalDefinition.Name is "ReadOnlyCollection" or "ReadOnlyDictionary" or "ReadOnlyObservableCollection"
                    or "Memory" or "ReadOnlyMemory" or "ArraySegment");
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
            if (ReferenceEquals(parent, scope) || IsNestedFunction(parent))
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
        return escape is IThrowOperation thrown
            ? CatchesTheThrow(tryOperation, thrown)
            : HasAbsorbingCatch(tryOperation, CatchesEverything);
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
        return ThrownType(thrown) is { } thrownType
            && HasAbsorbingCatch(tryOperation, caughtType => Inherits(thrownType, caughtType));
    }

    // An unfiltered handler that resumes after the try and whose type admits the exception.
    private static bool HasAbsorbingCatch(ITryOperation tryOperation, Func<ITypeSymbol, bool> admits)
    {
        return tryOperation.Catches.Any(catchClause => catchClause.Filter is null
            && CanResume(catchClause)
            && (catchClause.ExceptionType is not { } caughtType || admits(caughtType)));
    }

    // The thrown operand hides behind the implicit conversion to System.Exception: the
    // CONVERTED type would never match a derived catch.
    private static INamedTypeSymbol? ThrownType(IThrowOperation thrown)
    {
        return PeelConversions(thrown.Exception)?.Type as INamedTypeSymbol;
    }

    private static bool Inherits(INamedTypeSymbol thrownType, ITypeSymbol caughtType)
    {
        for (ITypeSymbol? current = thrownType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, caughtType))
            {
                return true;
            }
        }

        return false;
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
        return Ancestors(operation).FirstOrDefault(IsNestedFunction) ?? block;
    }

    private static bool ReportUsesAfter(OperationBlockAnalysisContext context, TransferEntry entry, List<IOperation> scanRoots)
    {
        var (variable, transferPosition, scope) = (entry.Variable, entry.Position, entry.Scope);
        var candidates = scanRoots.SelectMany(root => root.DescendantsAndSelf()).ToList();

        // Source-ordered walk of every later reference on the transferred path. A
        // local-function DECLARATION does not execute: its body's references count only
        // through actual invocations (below). Lambda bodies stay direct references -- a
        // delegate built after the transfer escapes into code that can only run after it.
        var laterReferences = candidates
            .Where(operation => IsReferenceTo(operation, variable)
                && operation.Syntax.SpanStart >= transferPosition
                && !IsInsideNameOf(operation)
                && RunsOnTheTransferredPath(operation, entry, scanRoots))
            .Select(operation => (Operation: operation, CallEffect: default(BodyEffect?)));

        // A call to a local function (or through a delegate local) whose body reads the
        // variable is a use AT THE CALL: the body's reference sits source-BEFORE the transfer
        // when declared earlier, so the position filter alone would hide it. A body that
        // definitely REWRITES makes the call a reinitialization instead.
        var callableCalls = candidates
            .OfType<IInvocationOperation>()
            .Where(invocation => invocation.TargetMethod.MethodKind is MethodKind.LocalFunction or MethodKind.DelegateInvoke
                // A call WRAPPING the transfer (Use(Sending.Transfer(list))) starts before it,
                // but its body runs only after all arguments -- the handoff included.
                && (invocation.Syntax.SpanStart >= transferPosition || Covers(invocation, transferPosition))
                // A PROPAGATED transfer's own call site is the handoff, not a use of it.
                && !ReferenceEquals(invocation, entry.Transfer)
                && RunsOnTheTransferredPath(invocation, entry, scanRoots))
            .Select(invocation => (Operation: (IOperation)invocation,
                CallEffect: (BodyEffect?)CallEffectOf(invocation, variable, transferPosition, scope)))
            .Where(call => call.CallEffect != BodyEffect.None);

        var orderedUses = laterReferences.Concat(callableCalls)
            .Concat(EscapeUses(candidates, entry, scanRoots))
            .OrderBy(use => use.Operation.Syntax.SpanStart);

        var dominated = new DominatedRegions();
        foreach (var (reference, callEffect) in orderedUses)
        {
            if (dominated.Cover(reference))
            {
                continue;
            }

            if (callEffect is { } effect)
            {
                if (LocalFunctionCallOutcome(context, reference, variable, effect, transferPosition, scope, dominated) is { } outcome)
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
                    if (CatchUseDuringReinitialization(reference.Parent!, variable, transferPosition, scope) is { } windowUse)
                    {
                        Report(context, windowUse, variable);
                        return true;
                    }

                    return false; // a definite reinitialization: everything after is a new value
                case ReferenceRole.ConditionalReinitialization:
                    dominated.Add(reference.Parent!, reference.Syntax.Span.End);
                    continue; // may not have run on the path that transferred: keep scanning
                default:
                    Report(context, rhsUse ?? reference, variable);
                    return true; // one report per transfer keeps the noise proportional
            }
        }

        return false;
    }

    // Whether the operation runs on the path the transfer took: not in a mutually exclusive
    // sibling arm of the construct holding the transfer (`if (move) Transfer(list); else
    // list.Add(1);` -- try constructs are not excluded, an exception after the transfer
    // reaches the handlers and finally), not in a catch no post-transfer failure can reach
    // (unless the transfer itself escapes into the handlers), and not inside a
    // local-function body the scan treats as a declaration.
    private static bool RunsOnTheTransferredPath(IOperation operation, TransferEntry entry, List<IOperation> scanRoots)
    {
        return !IsWithinALocalFunctionBody(operation, scanRoots)
            && !IsInASiblingArmOfTheTransfer(operation, entry.Position)
            && (entry.Escaped || !IsInAnUnreachableCatch(operation, entry.Position));
    }

    // Post-transfer ESCAPES of callables that captured the transferred variable: a READING
    // local function converted to a delegate, or a stored delegate reference leaving the
    // scope -- both can run later against the receiver-owned object. Calls are classified
    // separately; a bare store target rewrites the local without running anything, and a
    // resetting body makes no promise (it may never run).
    private static IEnumerable<(IOperation Operation, BodyEffect? CallEffect)> EscapeUses(
        List<IOperation> candidates, TransferEntry entry, List<IOperation> scanRoots)
    {
        var (variable, transferPosition, scope) = (entry.Variable, entry.Position, entry.Scope);
        var methodGroupEscapes = candidates
            .OfType<IMethodReferenceOperation>()
            .Where(reference => reference.Method.MethodKind == MethodKind.LocalFunction
                && reference.Syntax.SpanStart >= transferPosition
                && RunsOnTheTransferredPath(reference, entry, scanRoots)
                && LocalFunctionCallEffect(reference.Method, variable, scope, NewSymbolSet<IMethodSymbol>()) == BodyEffect.Reads)
            .Select(reference => (Operation: (IOperation)reference, CallEffect: (BodyEffect?)BodyEffect.Reads));

        var delegateEscapes = candidates
            .Where(reference => reference is ILocalReferenceOperation or IParameterReferenceOperation
                && DelegateSymbolOf(reference) is { } delegateSymbol
                && reference.Syntax.SpanStart >= transferPosition
                && EscapesTheScope(reference, scope)
                && RunsOnTheTransferredPath(reference, entry, scanRoots)
                && AnyStoredCallableReads(delegateSymbol, variable, reference, transferPosition, scope))
            .Select(reference => (Operation: reference, CallEffect: (BodyEffect?)BodyEffect.Reads));

        return methodGroupEscapes.Concat(delegateEscapes);
    }

    private static BodyEffect CallEffectOf(IInvocationOperation invocation, ISymbol variable, int transferPosition, IOperation scope)
    {
        return invocation.TargetMethod.MethodKind == MethodKind.LocalFunction
            ? LocalFunctionCallEffect(invocation.TargetMethod, variable, scope, NewSymbolSet<IMethodSymbol>())
            : DelegateCallEffect(invocation, variable, transferPosition, scope);
    }

    // A reading call is the use itself -- unless its own ARGUMENTS definitely reset the
    // variable first, feeding the body the fresh value. A resetting call (by body or by
    // argument) ends tracking exactly like an inline reinitialization -- when the CALL
    // definitely runs; a conditional one dominates only its own arm -- minus the throw
    // window: the callee can throw into an enclosing catch before its reset landed, and the
    // handler then still observes the transferred value. Null: the scan continues.
    private static bool? LocalFunctionCallOutcome(OperationBlockAnalysisContext context, IOperation call, ISymbol variable, BodyEffect effect,
        int transferPosition, IOperation scope, DominatedRegions dominated)
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
            dominated.Add(call, call.Syntax.Span.End);
            return null;
        }

        if (CatchUseDuringReinitialization(call, variable, transferPosition, scope) is { } windowUse)
        {
            Report(context, windowUse, variable);
            return true;
        }

        // The callee can fail BEFORE its reset lands: a caller-side catch that resumes then
        // continues with the ORIGINAL value still in the variable -- the reset is only
        // conditional for such callers.
        if (effect == BodyEffect.Resets
            && CalleeCanThrowBeforeItsReset(call, variable, scope)
            && ACaughtFailureCanResumePast(call, call.Syntax.Span.End))
        {
            dominated.Add(call, call.Syntax.Span.End);
            return null;
        }

        return false;
    }

    private static bool CalleeCanThrowBeforeItsReset(IOperation call, ISymbol variable, IOperation scope)
    {
        if (call is not IInvocationOperation { TargetMethod: { MethodKind: MethodKind.LocalFunction } target })
        {
            return false;
        }

        if (LocalFunctionDeclarationIn(scope, target) is not { } declaration)
        {
            return false;
        }

        var reset = declaration.DescendantsAndSelf().FirstOrDefault(operation =>
            IsReferenceTo(operation, variable)
            && ResetShapeOf(operation, variable, declaration.Syntax.SpanStart, out _) is not null);
        var limit = reset?.Syntax.SpanStart ?? int.MaxValue;

        return declaration.DescendantsAndSelf().Any(operation =>
            operation.Syntax.Span.End <= limit
            && !IsWithinANestedFunction(operation, declaration)
            && CanThrow(operation));
    }

    // Arguments evaluate BEFORE the callee runs: a read among them is a use no body effect
    // can excuse, and a definite reset among them hands every later evaluation -- the body
    // included -- the fresh value.
    private static BodyEffect ArgumentsEffect(IInvocationOperation invocation, ISymbol variable, int floorPosition, out IOperation? read)
    {
        read = null;
        var references = invocation.Arguments
            .SelectMany(argument => ReadsOf(argument.Value, variable))
            .Where(operation => operation.Syntax.SpanStart >= floorPosition
                // An OUT write lands only when the callee returns -- AFTER its body ran --
                // so it can never feed the body a fresh value. The call-site reference is
                // classified as a reinitialization by the direct scan instead.
                && operation.Parent is not IArgumentOperation { Parameter.RefKind: RefKind.Out })
            .OrderBy(operation => operation.Syntax.SpanStart);

        foreach (var reference in references)
        {
            var reset = ResetShapeOf(reference, variable, invocation.Syntax.SpanStart, out read);
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

    // Regions where a SKIPPED conditional reinitialization dominates: inside its own arm,
    // after it, the variable is fresh on every path that reaches a reference
    // (`if (reset) { list = new(); list.Add(1); }` -- the Add never sees the transferred
    // list), so such references are clean while the scan continues past the arm.
    private sealed class DominatedRegions
    {
        private readonly List<(TextSpan Region, int Position)> entries = new();

        public void Add(IOperation reinitialization, int position)
        {
            entries.Add((DominatingRegion(reinitialization).Syntax.Span, position));
        }

        public bool Cover(IOperation reference)
        {
            var start = reference.Syntax.SpanStart;
            return entries.Any(entry => start >= entry.Position && entry.Region.Contains(start));
        }
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
            if (parent is ITryOperation { Body: { } body } && child is ICatchClauseOperation clause
                && Covers(body, transferPosition)
                && !ThrowCapableOpsAfter(body, transferPosition).Any(operation => FailureReaches(operation, clause)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<IOperation> ThrowCapableOpsAfter(IOperation region, int transferPosition)
    {
        return region.DescendantsAndSelf().Where(operation =>
            operation.Syntax.Span.End > transferPosition
            && CanThrow(operation)
            // an explicit throw governs its subtree: its operand is not an independent failure
            && (operation is IThrowOperation || !IsInsideAThrow(operation))
            && !IsWithinANestedFunction(operation, region)
            && !IsInASiblingArmOfTheTransfer(operation, transferPosition));
    }

    // An EXPLICIT throw carries its type: an unfiltered catch of an unrelated type can
    // never receive it. Every other failure's type is unknowable and reaches any clause.
    private static bool FailureReaches(IOperation operation, ICatchClauseOperation clause)
    {
        return operation is not IThrowOperation thrown || ClauseCanCatch(clause, thrown);
    }

    private static bool ClauseCanCatch(ICatchClauseOperation catchClause, IThrowOperation thrown)
    {
        // C# tests the clause TYPE before evaluating any filter: a filtered clause of an
        // unrelated type never receives the throw -- the filter only matters (and may
        // pass) once the type gate admits it. An unknowable thrown type reaches any clause.
        return ThrownType(thrown) is not { } thrownType
            || catchClause.ExceptionType is not { } caughtType
            || caughtType.SpecialType == SpecialType.System_Object
            || Inherits(thrownType, caughtType);
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

        var dominated = new DominatedRegions();
        foreach (var current in events)
        {
            if (dominated.Cover(current))
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

            dominated.Add(reset!, current.Syntax.Span.End);
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

        return IsReferenceTo(operation, variable)
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

        var reset = ResetShapeOf(current, variable, region.Syntax.SpanStart, out var resetRead);
        if (reset is null)
        {
            return (BodyEffect.Reads, current, null); // a plain read of the transferred value
        }

        return resetRead is not null
            ? (BodyEffect.Reads, resetRead, null) // the replacement is built FROM the transferred value
            : (BodyEffect.Resets, null, reset);
    }

    // The reset operation the reference belongs to (null when it is an ordinary read): a
    // simple-assignment target, an `out` argument (the callee must assign it and cannot read
    // it), or a deconstruction target. `read` carries the reference that still reads the
    // transferred value on the way in -- the reassignment's RHS, or a sibling argument
    // evaluated at or after `floorPosition` (the out-assignment happens only when the callee
    // RUNS, after every sibling argument was evaluated).
    private static IOperation? ResetShapeOf(IOperation reference, ISymbol variable, int floorPosition, out IOperation? read)
    {
        read = null;

        if (reference.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, reference))
        {
            read = ReadWithin(assignment.Value, variable);
            return assignment;
        }

        if (reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out } outArgument)
        {
            read = SiblingArgumentRead(outArgument, variable, floorPosition);
            return outArgument;
        }

        // A DYNAMIC call binds no parameters, so its `out` argument shows only in syntax: the
        // runtime binder assigns it whenever the call returns normally (the call itself is
        // the reset, so its failure window is the call's).
        if (reference.Parent is IDynamicInvocationOperation dynamicCall
            && reference.Syntax.Parent is ArgumentSyntax argument && argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword))
        {
            read = SiblingRead(dynamicCall.Arguments.Where(sibling => !ReferenceEquals(sibling, reference)), variable, floorPosition);
            return dynamicCall;
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
    private static IEnumerable<IOperation?> ObjectCreationCarriedParts(
        IObjectCreationOperation creation, HashSet<string>? alsoOverwritten = null)
    {
        if (creation.Initializer is { } initializer)
        {
            foreach (var part in InitializerCarriedParts(initializer))
            {
                yield return part;
            }
        }

        // Only the PRIMARY constructor's parameters deterministically store (its declaring
        // syntax IS the record declaration); a hand-written constructor with a matching
        // parameter name promises nothing.
        // Known framework VALUE CARRIERS store every constructor argument by contract:
        // KeyValuePair/Tuple/ValueTuple are tuple syntax in another spelling.
        if (creation.Constructor is { ContainingType: { } carrierType }
            && carrierType.Name is "KeyValuePair" or "ValueTuple" or "Tuple"
            && IsFrameworkDeclared(carrierType))
        {
            foreach (var argument in creation.Arguments)
            {
                yield return argument.Value;
            }

            yield break;
        }

        foreach (var carried in RecordConstructorCarriedParts(creation, alsoOverwritten))
        {
            yield return carried;
        }
    }

    private static IEnumerable<IOperation?> RecordConstructorCarriedParts(
        IObjectCreationOperation creation, HashSet<string>? alsoOverwritten)
    {
        if (creation.Constructor is not { ContainingType: { IsRecord: true } recordType } constructor
            || !constructor.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax() is RecordDeclarationSyntax))
        {
            yield break;
        }

        var overwritten = OverwrittenSlots(creation.Initializer);
        if (alsoOverwritten is not null)
        {
            overwritten.UnionWith(alsoOverwritten);
        }

        foreach (var argument in creation.Arguments)
        {
            // Only the SYNTHESIZED positional property stores its parameter (its declaring
            // syntax IS the parameter); an explicitly redeclared property replaces the
            // storage and may never read it -- and an object initializer that definitely
            // OVERWRITES the slot drops the constructor's value before the object escapes.
            if (argument.Parameter is { } parameter
                && !overwritten.Contains(parameter.Name)
                && recordType.GetMembers(parameter.Name).OfType<IPropertySymbol>().Any(property =>
                    SendableSymbolClassifier.HasBackingSlot(property)
                    && property.DeclaringSyntaxReferences.Any(reference =>
                        reference.GetSyntax() is ParameterSyntax)))
            {
                yield return argument.Value;
            }
        }
    }

    // Member writes carry through compiler-known slots only; an INDEXER initializer
    // (["x"] = list) stores value AND keys into the collection; a collection initializer's
    // Add elements carry like collection expressions.
    private static IEnumerable<IOperation?> InitializerCarriedParts(IObjectOrCollectionInitializerOperation initializer)
    {
        return initializer.Initializers.SelectMany(element => element switch
        {
            // Indexer sets and Add calls are KNOWN stores only on framework collections: a
            // user-defined Add/setter may drop what it was handed, like a custom setter.
            ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true } indexer } member
                when IsFrameworkDeclared(indexer.Property.ContainingType) =>
                indexer.Arguments.Select(argument => (IOperation?)argument.Value).Concat(new[] { (IOperation?)member.Value }),
            ISimpleAssignmentOperation member when RetainsItsSlot(member.Target) => new[] { (IOperation?)member.Value },
            // Child = { Value = list }: writes INTO the object the transferred payload
            // already reaches -- when Child is a RETAINED slot. A computed member hands out
            // a temporary the payload never keeps.
            IMemberInitializerOperation { Initializer: { } nested } memberInitializer
                when RetainsItsSlot(memberInitializer.InitializedMember) => InitializerCarriedParts(nested),
            IInvocationOperation add when IsFrameworkDeclared(add.TargetMethod.ContainingType) =>
                add.Arguments.Select(argument => (IOperation?)argument.Value),
            _ => Enumerable.Empty<IOperation?>(),
        });
    }

    // Types from the framework's own collection assemblies: their contracts store what
    // they are handed. Anchored on the CORE LIBRARY's assembly identity plus the framework
    // collection assemblies by exact name -- a System.* NAMESPACE in a user assembly proves
    // nothing.
    private static bool IsFrameworkDeclared(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.ContainingAssembly?.Identity.Name is
            "System.Runtime" or "System.Collections" or "System.Collections.Concurrent"
            or "System.Collections.Immutable" or "System.Collections.Specialized"
            or "System.Collections.NonGeneric" or "System.Linq" or "netstandard" or "mscorlib")
        {
            return true;
        }

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                return SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, current.ContainingAssembly);
            }
        }

        return false;
    }

    private static IEnumerable<IOperation?> WithCarriedParts(IWithOperation withOperation)
    {
        var operand = PeelConversions(withOperation.Operand);

        // The clone SHALLOW-COPIES the operand's slots: a compound operand unfolds its
        // carried parts -- minus slots the with-initializer definitely OVERWRITES -- and a
        // leaf operand (a record variable) shares its reference-typed contents with the
        // receiver-owned clone, so the variable itself is a source.
        var overwritten = OverwrittenSlots(withOperation.Initializer);
        var operandParts = operand switch
        {
            null => Enumerable.Empty<IOperation?>(),
            IObjectCreationOperation creation => ObjectCreationCarriedParts(creation, overwritten),
            _ => CarriedParts(operand) ?? new[] { (IOperation?)operand },
        };

        return withOperation.Initializer is { } initializer
            ? operandParts.Concat(InitializerCarriedParts(initializer))
            : operandParts;
    }

    private static HashSet<string> OverwrittenSlots(IObjectOrCollectionInitializerOperation? initializer)
    {
        return new HashSet<string>(
            (initializer?.Initializers ?? ImmutableArray<IOperation>.Empty)
                .OfType<ISimpleAssignmentOperation>()
                .Select(member => (member.Target as IPropertyReferenceOperation)?.Property.Name)
                .Where(name => name is not null)!);
    }

    private static bool RetainsItsSlot(IOperation? member)
    {
        return member is IFieldReferenceOperation
            || (member is IPropertyReferenceOperation property && SendableSymbolClassifier.HasBackingSlot(property.Property));
    }

    // Enumerating a source INTO a new collection ([..source], CreateRange(source),
    // source.ToImmutableArray()) never carries the source OBJECT, but an inline container's
    // elements are delivered -- [.. new[] { list }] hands the receiver the same list
    // reference. So only the source's own carried parts unfold; a plain variable contributes
    // nothing.
    private static IEnumerable<IOperation?> CopiedElementParts(IOperation? source)
    {
        var operand = PeelConversions(source);
        return operand is not null && CarriedParts(operand) is { } parts
            ? parts
            : Enumerable.Empty<IOperation?>();
    }

    private static IOperation? ReadWithin(IOperation expression, ISymbol variable)
    {
        return ReadsOf(expression, variable).FirstOrDefault();
    }

    // A reference inside a nested local function that nothing in the region ever invokes (or
    // lifts into a delegate) can never run by entering the region: the declaration alone
    // executes nothing. Lambda bodies stay visible -- building the delegate is itself the
    // escape.
    private static bool IsInAnUnreferencedNestedFunction(IOperation reference, IOperation region)
    {
        return EnclosingLocalFunctions(reference, region).Any(nested => !IsReferencedWithin(region, nested.Symbol));
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
        var definition = localFunction.OriginalDefinition;
        if (LocalFunctionDeclarationIn(scope, localFunction) is not { } declaration || !inProgress.Add(definition))
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

        return AnyStoredCallableReads(delegateLocal, variable, invocation, transferPosition, scope)
            ? BodyEffect.Reads
            : BodyEffect.None;
    }

    private static bool AnyStoredCallableReads(ISymbol delegateLocal, ISymbol variable, IOperation consumer, int transferPosition, IOperation scope)
    {
        return StoredCallables(delegateLocal, scope, consumer, transferPosition, NewSymbolSet<ISymbol>())
            .Any(callable => callable switch
            {
                IAnonymousFunctionOperation lambda =>
                    EffectOf(lambda, variable, scope, NewSymbolSet<IMethodSymbol>(), out _) == BodyEffect.Reads,
                IMethodReferenceOperation { Method: { MethodKind: MethodKind.LocalFunction } method } =>
                    LocalFunctionCallEffect(method, variable, scope, NewSymbolSet<IMethodSymbol>()) == BodyEffect.Reads,
                _ => false,
            });
    }

    // The delegate-typed local or parameter a reference names.
    private static ISymbol? DelegateSymbolOf(IOperation reference)
    {
        var symbol = ReferencedVariable(reference);
        var type = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null,
        };

        return type is { TypeKind: TypeKind.Delegate } ? symbol : null;
    }

    // A delegate reference ESCAPES when it is returned, passed as an argument, or stored
    // into a non-local slot -- merely inspecting it (use != null) runs nothing, a local
    // copy is an alias resolved at ITS uses, and calls/stores are classified separately.
    private static bool EscapesTheScope(IOperation reference, IOperation scope)
    {
        for (IOperation child = reference; child.Parent is { } parent; child = parent)
        {
            switch (parent)
            {
                case IConversionOperation or IDelegateCreationOperation or ITupleOperation
                    or IArrayInitializerOperation or IObjectOrCollectionInitializerOperation
                    or IArrayCreationOperation or IAnonymousObjectCreationOperation:
                    continue; // wrappers: the payload rides along
                // value-selecting expressions: either arm can BE the result (`flag ? use
                // : null`), so the payload rides -- a reference in the CONDITION or the
                // governing value is only examined, never published.
                case IConditionalOperation conditional when !ReferenceEquals(child, conditional.Condition):
                    continue;
                case ICoalesceOperation:
                    continue;
                case ISwitchExpressionArmOperation arm when ReferenceEquals(child, arm.Value):
                    continue;
                case ISwitchExpressionOperation switchExpression when !ReferenceEquals(child, switchExpression.Value):
                    continue;
                case IReturnOperation { Kind: not OperationKind.YieldBreak }:
                    return true;
                case IArgumentOperation argument:
                    return !IsDroppedByAnInertLocalFunction(argument, scope);
                case ISimpleAssignmentOperation assignment when !ReferenceEquals(child, assignment.Target):
                    return ReferencedVariable(assignment.Target) is null; // a non-local slot publishes
                case ICompoundAssignmentOperation compound when !ReferenceEquals(child, compound.Target):
                    return ReferencedVariable(compound.Target) is null;
                default:
                    return false;
            }
        }

        return false;
    }

    // An argument to a SAME-SCOPE local function whose body never references the parameter
    // is dropped, not published: nothing can run or store a delegate its callee never
    // touches. Any reference at all keeps the escape (the body may invoke, store, or
    // forward it), and methods outside the scope stay opaque escapes.
    private static bool IsDroppedByAnInertLocalFunction(IArgumentOperation argument, IOperation scope)
    {
        if (argument.Parent is not IInvocationOperation { TargetMethod: { MethodKind: MethodKind.LocalFunction } target }
            || argument.Parameter is not { } parameter)
        {
            return false;
        }

        var definition = target.OriginalDefinition;
        if (LocalFunctionDeclarationIn(scope, target) is not { } declaration || parameter.Ordinal >= definition.Parameters.Length)
        {
            return false;
        }

        var declared = definition.Parameters[parameter.Ordinal];
        return !declaration.DescendantsAndSelf().Any(operation =>
            operation is IParameterReferenceOperation reference
            && SymbolEqualityComparer.Default.Equals(reference.Parameter, declared));
    }

    // The slot the invocation calls through -- behind a ?. receiver too: use?.Invoke() puts
    // a placeholder in Instance, and the real receiver hangs off the enclosing conditional
    // access.
    private static ISymbol? DelegateReceiver(IInvocationOperation invocation)
    {
        var receiver = invocation.Instance is IConditionalAccessInstanceOperation
            ? Ancestors(invocation).OfType<IConditionalAccessOperation>().FirstOrDefault()?.Operation
            : invocation.Instance;
        return ReferencedSlot(receiver);
    }

    // The slot a delegate lives in: a local, a parameter, or a field/property on the
    // method's OWN instance (`this.use` and bare `use` name the same slot; another
    // instance's slot is not this one). Cross-method mutation of such a slot stays
    // invisible, matching the best-effort store model.
    private static ISymbol? ReferencedSlot(IOperation? reference)
    {
        if (ReferencedVariable(reference) is { } variable)
        {
            return variable;
        }

        return reference switch
        {
            IFieldReferenceOperation { Instance: null or IInstanceReferenceOperation } field => field.Field,
            // Only a BACKING slot stores what its setter was handed: custom accessors may
            // discard the assignment and manufacture something else entirely.
            IPropertyReferenceOperation { Instance: null or IInstanceReferenceOperation } property
                when SendableSymbolClassifier.HasBackingSlot(property.Property) => property.Property,
            _ => null,
        };
    }

    // Every callable stored in the delegate local that can still be its value AT THE CALL:
    // stores in a sibling arm of the transfer never run on the transferred path, a store
    // that only happens after the call cannot be what it invoked (unless a loop carries it
    // back around), a definite `=` overwrite KILLS every earlier target, `-=` never adds
    // one, and a plain delegate-to-delegate copy carries the SOURCE local's candidates.
    private static IEnumerable<IOperation> StoredCallables(
        ISymbol delegateLocal, IOperation scope, IOperation consumer, int transferPosition, HashSet<ISymbol> visited)
    {
        if (!visited.Add(delegateLocal))
        {
            yield break;
        }

        var stores = DelegateStores(delegateLocal, scope, consumer, transferPosition);

        // A definite overwrite replaces the whole invocation list: candidates begin at the
        // LAST replace that runs on every path TO THE CALL -- a branch-local overwrite
        // counts when the call lives in the same arm (`if (flag) { use = ...; use(); }`).
        var killPoint = stores
            .Where(store => store.Replaces && !IsConditionalWithin(store.Site, scope, towards: consumer))
            .Select(store => store.Site.Syntax.SpanStart)
            .DefaultIfEmpty(int.MinValue)
            .Max();

        var cancelled = CancelledComponents(stores, delegateLocal, scope, consumer);

        for (var index = 0; index < stores.Count; index++)
        {
            var (site, value, _) = stores[index];
            if (site.Syntax.SpanStart < killPoint)
            {
                continue;
            }

            var components = ResolvedCallables(value, site, scope, transferPosition, visited).ToList();
            if (cancelled.TryGetValue(index, out var removedMethods))
            {
                RemoveLastOccurrences(components, removedMethods);
            }

            foreach (var resolved in components)
            {
                yield return resolved;
            }
        }
    }

    private static IEnumerable<IOperation> ResolvedCallables(
        IOperation value, IOperation site, IOperation scope, int transferPosition, HashSet<ISymbol> visited)
    {
        foreach (var item in Callables(value))
        {
            // Action use = use2 (alone or inside a combine): the copy SNAPSHOTS what the
            // source holds AT THE COPY -- stores landing in the source afterwards are not
            // in use's invocation list.
            if (ReferencedSlot(item) is { } alias)
            {
                foreach (var carried in StoredCallables(alias, scope, site, transferPosition, visited))
                {
                    yield return carried;
                }

                continue;
            }

            yield return item;
        }
    }

    // A definite `-= M` strips ONE occurrence of M -- Delegate.Remove semantics: the last
    // occurrence definitely present among the prior stores, combined invocation lists
    // included (only method groups are identifiable; removing a fresh lambda instance
    // removes nothing, and an occurrence under a conditional arm is not definitely there
    // to strip). Duplicate subscriptions survive their single removal.
    private static Dictionary<int, List<IMethodSymbol>> CancelledComponents(
        List<(IOperation Site, IOperation Value, bool Replaces)> stores, ISymbol delegateLocal, IOperation scope, IOperation consumer)
    {
        var cancelled = new Dictionary<int, List<IMethodSymbol>>();
        foreach (var removal in DelegateRemovals(delegateLocal, scope, consumer).OrderBy(removal => removal.Position))
        {
            for (var index = stores.Count - 1; index >= 0; index--)
            {
                if (stores[index].Site.Syntax.SpanStart < removal.Position
                    && RemainingDefiniteOccurrences(stores[index].Value, removal.Method, cancelled, index) > 0)
                {
                    if (!cancelled.TryGetValue(index, out var methods))
                    {
                        cancelled[index] = methods = new List<IMethodSymbol>();
                    }

                    methods.Add(removal.Method);
                    break;
                }
            }
        }

        return cancelled;
    }

    private static int RemainingDefiniteOccurrences(
        IOperation value, IMethodSymbol method, Dictionary<int, List<IMethodSymbol>> cancelled, int index)
    {
        var present = DefiniteMethodGroups(value)
            .Count(group => SymbolEqualityComparer.Default.Equals(group.Method.OriginalDefinition, method));
        var taken = cancelled.TryGetValue(index, out var methods)
            ? methods.Count(taken => SymbolEqualityComparer.Default.Equals(taken, method))
            : 0;
        return present - taken;
    }

    // The method groups DEFINITELY in a stored value's invocation list: `+` combines carry
    // both sides and wrappers are transparent, but a conditional's arms are ALTERNATIVES.
    private static IEnumerable<IMethodReferenceOperation> DefiniteMethodGroups(IOperation? stored)
    {
        switch (UnwrapDelegate(stored))
        {
            case IMethodReferenceOperation group:
                yield return group;
                break;
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } combine:
                foreach (var group in DefiniteMethodGroups(combine.LeftOperand).Concat(DefiniteMethodGroups(combine.RightOperand)))
                {
                    yield return group;
                }

                break;
        }
    }

    private static void RemoveLastOccurrences(List<IOperation> components, List<IMethodSymbol> removedMethods)
    {
        foreach (var method in removedMethods)
        {
            for (var position = components.Count - 1; position >= 0; position--)
            {
                if (components[position] is IMethodReferenceOperation group
                    && SymbolEqualityComparer.Default.Equals(group.Method.OriginalDefinition, method))
                {
                    components.RemoveAt(position);
                    break;
                }
            }
        }
    }

    private static List<(int Position, IMethodSymbol Method)> DelegateRemovals(ISymbol delegateLocal, IOperation scope, IOperation consumer)
    {
        var removals = new List<(int Position, IMethodSymbol Method)>();
        foreach (var operation in scope.DescendantsAndSelf())
        {
            if (operation is ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Subtract } removal
                && SymbolEqualityComparer.Default.Equals(ReferencedSlot(removal.Target), delegateLocal)
                && operation.Syntax.Span.End <= consumer.Syntax.SpanStart
                && !IsConditionalWithin(removal, scope)
                && Callable(removal.Value) is IMethodReferenceOperation methodReference)
            {
                removals.Add((operation.Syntax.SpanStart, methodReference.Method.OriginalDefinition));
            }
        }

        return removals;
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
                    when SymbolEqualityComparer.Default.Equals(ReferencedSlot(assignment.Target), delegateLocal)
                    => (assignment.Value, true),
                // use += adds a target; -= only removes, storing nothing.
                ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add } combined
                    when SymbolEqualityComparer.Default.Equals(ReferencedSlot(combined.Target), delegateLocal)
                    => (combined.Value, false),
                _ => null,
            };

            if (matched is { } store
                && !IsInASiblingArmOfTheTransfer(operation, transferPosition)
                && (operation.Syntax.SpanStart < consumer.Syntax.Span.End || SharesALoopWith(operation, consumer))
                && NestedStoreCanBeCurrent(operation, scope, consumer))
            {
                stores.Add((operation, store.Value, store.Replaces));
            }
        }

        return stores;
    }

    // A store inside a nested local function is in the list at the call only if its
    // function can have RUN by then: an invocation source-before the consumer (or carried
    // back around by a loop) qualifies -- transitively so when that invocation itself sits
    // in a nested function -- and a method-group lift may travel and run anywhere. A
    // function nothing references cannot have run at all.
    private static bool NestedStoreCanBeCurrent(IOperation store, IOperation scope, IOperation consumer)
    {
        return EnclosingLocalFunctions(store, scope)
            .All(nested => CanHaveRunBefore(nested.Symbol, scope, consumer, NewSymbolSet<IMethodSymbol>()));
    }

    private static bool CanHaveRunBefore(IMethodSymbol localFunction, IOperation scope, IOperation consumer, HashSet<IMethodSymbol> visited)
    {
        if (!visited.Add(localFunction.OriginalDefinition))
        {
            return false;
        }

        return scope.DescendantsAndSelf().Any(operation => operation switch
        {
            IInvocationOperation invocation
                when SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, localFunction.OriginalDefinition)
                => (operation.Syntax.SpanStart < consumer.Syntax.Span.End || SharesALoopWith(operation, consumer))
                    && NestedCallSiteCanRun(operation, scope, consumer, visited),
            IMethodReferenceOperation methodReference
                => SymbolEqualityComparer.Default.Equals(methodReference.Method.OriginalDefinition, localFunction.OriginalDefinition),
            _ => false,
        });
    }

    private static bool NestedCallSiteCanRun(IOperation callSite, IOperation scope, IOperation consumer, HashSet<IMethodSymbol> visited)
    {
        return EnclosingLocalFunctions(callSite, scope).All(nested => CanHaveRunBefore(nested.Symbol, scope, consumer, visited));
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

    // Every callable a stored value can BE at runtime: `flag ? Touch : fallback` puts
    // either arm in the delegate, `a ?? b` likewise.
    private static IEnumerable<IOperation> Callables(IOperation? stored)
    {
        stored = UnwrapDelegate(stored);
        switch (stored)
        {
            case IAnonymousFunctionOperation or IMethodReferenceOperation:
                yield return stored;
                break;
            // read + noop keeps BOTH operands in the invocation list.
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } combine:
                foreach (var callable in Callables(combine.LeftOperand).Concat(Callables(combine.RightOperand)))
                {
                    yield return callable;
                }

                break;
            case IConditionalOperation { WhenFalse: { } whenFalse } conditional:
                foreach (var callable in Callables(conditional.WhenTrue).Concat(Callables(whenFalse)))
                {
                    yield return callable;
                }

                break;
            case ICoalesceOperation coalesce:
                foreach (var callable in Callables(coalesce.Value).Concat(Callables(coalesce.WhenNull)))
                {
                    yield return callable;
                }

                break;
            // a delegate-typed variable inside a compound store: an ALIAS the caller resolves.
            case ILocalReferenceOperation or IParameterReferenceOperation:
                yield return stored;
                break;
        }
    }

    private static IOperation? Callable(IOperation? stored)
    {
        var callable = UnwrapDelegate(stored);
        return callable is IAnonymousFunctionOperation or IMethodReferenceOperation ? callable : null;
    }

    // Whether control can reach past the operation without executing it, judged within the
    // region entered at its top: any branching ancestor below the region root makes it
    // skippable, a nested function's body is deferred entirely, and a try counts only where
    // a path can resume past a failure (catch regions, bodies WITH catches). Asked ON THE
    // WAY TO a consumer, an arm the consumer itself lives in ran on every path that reaches
    // it -- from that ancestor upward, operation and consumer share every branch.
    private static bool IsConditionalWithin(IOperation operation, IOperation region, IOperation? towards = null)
    {
        for (IOperation child = operation; child.Parent is { } parent && !ReferenceEquals(parent, region); child = parent)
        {
            if (IsNestedFunction(parent))
            {
                return true;
            }

            if (towards is not null && child.Syntax.Span.Contains(towards.Syntax.Span))
            {
                return false;
            }

            if (parent is ITryOperation tryOperation)
            {
                if (!RunsToCompletionOrExits(tryOperation, child))
                {
                    return true;
                }

                continue;
            }

            if (IsBranching(parent))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideNameOf(IOperation operation)
    {
        return Ancestors(operation).Any(parent => parent is INameOfOperation);
    }

    // A local-function declaration or lambda body does not execute (or throw, or exit a loop)
    // on the enclosing path: nothing inside one counts as code the enclosing flow runs.
    private static bool IsWithinANestedFunction(IOperation operation, IOperation boundary)
    {
        for (var current = operation; current is not null && !ReferenceEquals(current, boundary); current = current.Parent)
        {
            if (IsNestedFunction(current))
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
        return ThrowCapableOpsAfter(region, transferPosition).Any();
    }

    // The heuristic throw model shared by catch reachability and the reinitialization
    // windows: calls, allocations, awaits and member/element accesses that dereference --
    // a FIELD read only through a possibly-null receiver (`this` cannot be null) -- plus
    // user-defined conversions/operators and explicit reference downcasts.
    private static bool CanThrow(IOperation operation)
    {
        return operation switch
        {
            // Instance fields dereference REFERENCE-type receivers (`this` and struct locals
            // cannot be null; the receiver expression is modeled on its own); a STATIC
            // field's first touch can run a throwing type initializer.
            IFieldReferenceOperation field => field.Instance switch
            {
                null => true,
                IInstanceReferenceOperation => false,
                { Type.IsValueType: true } => false,
                _ => true,
            },
            IConversionOperation conversion => conversion.OperatorMethod is not null || conversion.IsChecked
                || (conversion.Conversion is { IsReference: true, IsImplicit: false } && !conversion.IsTryCast),
            IUnaryOperation or IBinaryOperation or ICompoundAssignmentOperation or IIncrementOrDecrementOperation
                => ArithmeticCanThrow(operation),
            _ => operation is IInvocationOperation or IObjectCreationOperation or IAwaitOperation
                or IPropertyReferenceOperation or IArrayElementReferenceOperation or IThrowOperation
                or IDynamicInvocationOperation or IDynamicMemberReferenceOperation
                or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation
                or IEventAssignmentOperation
                // scope exits dispose: Dispose/DisposeAsync is user code and can throw;
                // a foreach hides MoveNext/Dispose calls on a possibly-custom enumerator;
                // a lock's hidden Monitor.Enter throws on a null gate
                or IUsingOperation or IUsingDeclarationOperation or IForEachLoopOperation
                or ILockOperation,
        };
    }

    // User-defined operators are calls, checked arithmetic overflows, and integral or decimal
    // division by zero throws -- floating-point division yields infinity instead.
    private static bool ArithmeticCanThrow(IOperation operation)
    {
        return operation switch
        {
            IUnaryOperation unary => unary.OperatorMethod is not null || unary.IsChecked,
            IIncrementOrDecrementOperation increment => increment.OperatorMethod is not null || increment.IsChecked,
            IBinaryOperation binary => binary.OperatorMethod is not null || binary.IsChecked
                || (binary.OperatorKind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && ThrowsOnAZeroDivisor(binary.Type)),
            ICompoundAssignmentOperation compound => compound.OperatorMethod is not null || compound.IsChecked
                || (compound.OperatorKind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && ThrowsOnAZeroDivisor(compound.Type)),
            _ => false,
        };
    }

    private static bool ThrowsOnAZeroDivisor(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        return type?.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
            or SpecialType.System_Char or SpecialType.System_Decimal;
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
                && !IsInACaseGuard(parent, transferPosition)
                // goto case/default re-enters a sibling arm: the arms are not mutually
                // exclusive when the transfer's own case jumps on.
                && !(parent is ISwitchOperation switchOperation && TheTransfersCaseJumpsOn(switchOperation, transferPosition)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TheTransfersCaseJumpsOn(ISwitchOperation switchOperation, int transferPosition)
    {
        var transferCase = switchOperation.Cases.FirstOrDefault(switchCase => Covers(switchCase, transferPosition));
        return transferCase is not null
            && transferCase.Descendants().Any(operation =>
                operation is IBranchOperation { BranchKind: BranchKind.GoTo }
                // only `goto case`/`goto default` re-enters an arm -- a plain goto label
                // leaves the switch -- and only when it belongs to THIS switch and can run
                // after the handoff.
                && operation.Syntax is GotoStatementSyntax gotoSyntax
                && gotoSyntax.CaseOrDefaultKeyword.RawKind != 0
                && operation.Syntax.SpanStart >= transferPosition
                && ReferenceEquals(NearestSwitch(operation), switchOperation));
    }

    private static ISwitchOperation? NearestSwitch(IOperation operation)
    {
        return Ancestors(operation).OfType<ISwitchOperation>().FirstOrDefault();
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
            _ => Enumerable.Empty<IOperation?>(),
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
            if (IsBranching(parent) || parent is ITryOperation || IsNestedFunction(parent))
            {
                return child;
            }
        }

        return reinitialization;
    }

    // The exception WINDOW between the transfer and a completed reinitialization: when the
    // reinitializing expression can throw (an invocation, creation or await), an enclosing
    // catch entered from the try BODY observes the still-transferred value.
    private static IOperation? CatchUseDuringReinitialization(IOperation reinitialization, ISymbol variable, int transferPosition, IOperation scope)
    {
        var reinitRoot = reinitialization is IArgumentOperation { Parent: { } call } ? call : reinitialization;
        // Only failures once the wrapper EXISTS open the window: a throw from the transfer
        // expression itself (or earlier RHS work) means nothing escaped, and operations in
        // sibling arms or nested functions never run on this path at all.
        var failures = ThrowCapableOpsAfter(reinitRoot, transferPosition).ToList();
        if (failures.Count == 0)
        {
            return null;
        }

        for (IOperation child = reinitRoot; child.Parent is { } parent && !ReferenceEquals(child, scope); child = parent)
        {
            if (IsNestedFunction(parent))
            {
                break;
            }

            if (parent is ITryOperation tryOperation && ReferenceEquals(child, tryOperation.Body)
                && WindowUseIn(tryOperation, variable, scope, failures) is { } use)
            {
                return use;
            }
        }

        return null;
    }

    // The first window region that actually READS the transferred value. A handler that
    // REWRITES the variable before touching it (catch { list = new(); ... }) is a recovery,
    // not a use -- the reinitialization classification applies inside the window too.
    private static IOperation? WindowUseIn(ITryOperation tryOperation, ISymbol variable, IOperation scope, List<IOperation> failures)
    {
        foreach (var region in WindowRegionsOf(tryOperation))
        {
            // A catch no failure of the window can reach never observes the value; the
            // finally runs regardless.
            if (region is ICatchClauseOperation clause && !failures.Any(failure => FailureReaches(failure, clause)))
            {
                continue;
            }

            if (EffectOf(region, variable, scope, NewSymbolSet<IMethodSymbol>(), out var read) == BodyEffect.Reads)
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
        var reset = ResetShapeOf(reference, variable, transferPosition, out rhsUse);
        return reset is null || rhsUse is not null
            ? ReferenceRole.Use
            : ReinitializationRole(reset, transferPosition, scope);
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

        return SiblingRead(arguments.Where(argument => !ReferenceEquals(argument, outArgument)).Select(argument => argument.Value), variable, transferPosition);
    }

    // Position-filtered: when the same invocation performs the handoff
    // (Reset(Sending.Transfer(list), out list)), the reference inside the transfer
    // argument itself is the handoff, not a post-transfer read.
    private static IOperation? SiblingRead(IEnumerable<IOperation> expressions, ISymbol variable, int transferPosition)
    {
        return expressions
            .SelectMany(expression => ReadsOf(expression, variable))
            .FirstOrDefault(operation => operation.Syntax.SpanStart >= transferPosition);
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
            // An op WRAPPING the transfer (MayThrow(Sending.Transfer(list))) completes -- and
            // can fail -- after the wrapper exists; the transfer itself (ending exactly AT the
            // position) cannot have let the wrapper escape when it throws.
            operation.Syntax.Span.End > transferPosition
            && operation.Syntax.Span.End <= reinitPosition
            // A jump in a mutually exclusive sibling arm of the transfer never runs on the
            // path that transferred (`if (move) Transfer(list); else break;`): it cannot skip
            // anything on that path.
            && !IsInASiblingArmOfTheTransfer(operation, transferPosition)
            && CanSkipPast(operation, reinitPosition, scope));
    }

    private static bool CanSkipPast(IOperation operation, int reinitPosition, IOperation scope)
    {
        if (CanThrow(operation))
        {
            return ACaughtFailureCanResumePast(operation, reinitPosition);
        }

        // Returning exits the enclosing function past the reset. Inside a CALLED body's scan
        // that means the call completes without rewriting -- the caller's later uses still
        // see the transferred value. In the OUTER scan those later uses are unreachable on
        // the returning path, so nothing is skipped for them.
        if (operation is IReturnOperation { Kind: not OperationKind.YieldReturn })
        {
            return IsNestedFunction(scope);
        }

        if (operation is not IBranchOperation branch)
        {
            return false;
        }

        if (branch.BranchKind == BranchKind.GoTo)
        {
            return true; // an arbitrary target is assumed able to skip the reinitialization
        }

        var exited = Ancestors(branch).FirstOrDefault(parent => branch.BranchKind == BranchKind.Break
            ? parent is ILoopOperation or ISwitchOperation
            : parent is ILoopOperation);
        return exited is not null && exited.Syntax.Span.Contains(reinitPosition);
    }

    // A caught FAILURE resumes AFTER its try: any throw-capable operation (an explicit
    // throw, a call that may fail, ...) skips a reset that try still contains. An EXPLICIT
    // throw carries its type -- a catch that cannot catch it absorbs nothing -- and it
    // governs its whole subtree (the operand evaluating is not an independent failure path
    // worth modeling).
    private static bool ACaughtFailureCanResumePast(IOperation operation, int reinitPosition)
    {
        if (operation is not IThrowOperation && IsInsideAThrow(operation))
        {
            return false;
        }

        return NearestCatchingTry(operation) is { } catchingTry
            && catchingTry.Syntax.Span.Contains(reinitPosition)
            && (operation is not IThrowOperation thrown || CatchesTheThrow(catchingTry, thrown));
    }

    private static bool IsInsideAThrow(IOperation operation)
    {
        return AncestorsWithinFunction(operation).Any(parent => parent is IThrowOperation);
    }

    private static ITryOperation? NearestCatchingTry(IOperation thrown)
    {
        for (IOperation child = thrown; child.Parent is { } parent; child = parent)
        {
            if (IsNestedFunction(parent))
            {
                return null; // a deferred body's throw does not run on this path
            }

            if (parent is ITryOperation tryOperation && ReferenceEquals(child, tryOperation.Body)
                && tryOperation.Catches.Any(CanResume))
            {
                return tryOperation;
            }
        }

        return null;
    }

    // A handler that ends in an unconditional throw/return never completes normally:
    // control does not resume after its try on that path (catch { throw; } re-escapes).
    private static bool CanResume(ICatchClauseOperation catchClause)
    {
        var last = catchClause.Handler.Operations.LastOrDefault();
        if (last is IExpressionStatementOperation expressionStatement)
        {
            last = expressionStatement.Operation;
        }

        return last is not IThrowOperation
            && last is not IReturnOperation { Kind: not OperationKind.YieldReturn };
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
            var branches = IsBranching(parent) || parent is ITryOperation;
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
        return PeelConversions(operation) switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            _ => null,
        };
    }

    private static bool IsReferenceTo(IOperation? operation, ISymbol variable)
    {
        return SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable);
    }

    // The references to the variable inside an expression -- nameof(list) excluded: a
    // compile-time constant never reads the object at runtime.
    private static IEnumerable<IOperation> ReadsOf(IOperation expression, ISymbol variable)
    {
        return expression.DescendantsAndSelf().Where(operation => IsReferenceTo(operation, variable) && !IsInsideNameOf(operation));
    }

    private static IEnumerable<IOperation> Ancestors(IOperation operation)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            yield return parent;
        }
    }

    // The ancestors on the operation's own function: a lambda or local-function boundary
    // ends the enclosing flow's reach.
    private static IEnumerable<IOperation> AncestorsWithinFunction(IOperation operation)
    {
        return Ancestors(operation).TakeWhile(parent => !IsNestedFunction(parent));
    }

    private static IEnumerable<ILocalFunctionOperation> EnclosingLocalFunctions(IOperation operation, IOperation boundary)
    {
        return Ancestors(operation).TakeWhile(parent => !ReferenceEquals(parent, boundary)).OfType<ILocalFunctionOperation>();
    }

    private static bool IsNestedFunction(IOperation operation)
    {
        return operation is IAnonymousFunctionOperation or ILocalFunctionOperation;
    }

    // The constructs whose arms may or may not run on a given path.
    private static bool IsBranching(IOperation operation)
    {
        return operation is IConditionalOperation or ISwitchOperation or ISwitchExpressionOperation
            or ILoopOperation or IConditionalAccessOperation;
    }

    // Built-in conversions only: a user-defined conversion yields a new value, not its operand.
    private static IOperation? PeelConversions(IOperation? operation)
    {
        while (operation is IConversionOperation { OperatorMethod: null } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    // The expression behind a stored delegate value's conversions and delegate creations.
    private static IOperation? UnwrapDelegate(IOperation? stored)
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
                default:
                    return stored;
            }
        }
    }

    private static HashSet<TSymbol> NewSymbolSet<TSymbol>()
        where TSymbol : class, ISymbol
    {
        return new HashSet<TSymbol>(SymbolEqualityComparer.Default);
    }

    // Use<int>() carries a CONSTRUCTED symbol; the declaration carries the definition.
    private static ILocalFunctionOperation? LocalFunctionDeclarationIn(IOperation scope, IMethodSymbol localFunction)
    {
        var definition = localFunction.OriginalDefinition;
        return scope.DescendantsAndSelf().OfType<ILocalFunctionOperation>()
            .FirstOrDefault(declaration => SymbolEqualityComparer.Default.Equals(declaration.Symbol, definition));
    }

    private static bool IsSendingType(ITypeSymbol? type)
    {
        return type is INamedTypeSymbol { OriginalDefinition: { Arity: 1 } definition }
            && FactoryMethods.IsLibraryType(definition, "MemoizR", "Sending");
    }

    private static bool IsSendingHelper(IMethodSymbol method)
    {
        return method.ContainingType is { Arity: 0 } type && FactoryMethods.IsLibraryType(type, "MemoizR", "Sending");
    }
}
