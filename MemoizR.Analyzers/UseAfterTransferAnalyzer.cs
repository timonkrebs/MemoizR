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
            foreach (var (variable, transferPosition, scope) in Transfers(block))
            {
                ReportUsesAfter(context, scope, variable, transferPosition);
            }
        }
    }

    // Every Sending<T> creation (constructor or Sending.Transfer) whose argument is a
    // local/parameter reference, with the position the transfer happens at and the scope its
    // report walk covers.
    private static IEnumerable<(ISymbol Variable, int Position, IOperation Scope)> Transfers(IOperation block)
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

            if (!isTransfer || ReferencedVariable(TransferTarget(argument)) is not { } variable)
            {
                continue;
            }

            var scope = EnclosingFunctionBody(operation, block);
            if (EscapesTheScope(operation, scope))
            {
                continue;
            }

            yield return (variable, operation.Syntax.Span.End, scope);
        }
    }

    // A transfer the flow immediately leaves behind (`return Sending.Transfer(list);` /
    // `throw`) has no sender-side continuation in its scope: every later reference is
    // unreachable on the path that transferred. (break/continue are NOT exits: control
    // resumes after the loop, where later uses remain reachable.)
    private static bool EscapesTheScope(IOperation transfer, IOperation scope)
    {
        for (var parent = transfer.Parent; parent is not null && !ReferenceEquals(parent, scope); parent = parent.Parent)
        {
            if (parent is IReturnOperation or IThrowOperation)
            {
                return true;
            }

            if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                break;
            }
        }

        return false;
    }

    // Transfer(list = new(...)): after the statement the variable ALIASES the transferred
    // value -- the assignment's target is what the sender keeps holding.
    private static IOperation? TransferTarget(IOperation? argument)
    {
        return argument is ISimpleAssignmentOperation assignment ? assignment.Target : argument;
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

    private static void ReportUsesAfter(OperationBlockAnalysisContext context, IOperation scope, ISymbol variable, int transferPosition)
    {
        // Source-ordered walk of every later reference. References in a MUTUALLY EXCLUSIVE
        // sibling arm of the construct holding the transfer are excluded upfront: in
        // `if (move) Transfer(list); else list.Add(1);` no path runs the else after the
        // transfer. (Try constructs are not excluded -- an exception after the transfer
        // reaches the handlers and finally.)
        var laterReferences = scope.DescendantsAndSelf()
            .Where(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                && operation.Syntax.SpanStart >= transferPosition
                && !IsInASiblingArmOfTheTransfer(operation, transferPosition))
            .OrderBy(operation => operation.Syntax.SpanStart);

        // Regions where a SKIPPED conditional reinitialization dominates: inside its own arm,
        // after it, the variable is fresh on every path that reaches the reference
        // (`if (reset) { list = new(); list.Add(1); }` -- the Add never sees the transferred
        // list), so such references are clean while the scan continues past the arm.
        List<(Microsoft.CodeAnalysis.Text.TextSpan Region, int Position)>? dominated = null;

        foreach (var reference in laterReferences)
        {
            if (IsDominatedByASkippedReinitialization(dominated, reference))
            {
                continue;
            }

            switch (Classify(reference, variable, transferPosition, scope, out var rhsUse))
            {
                case ReferenceRole.FreshValueFromHere:
                    return; // a definite reinitialization: everything after is a new value
                case ReferenceRole.ConditionalReinitialization:
                    (dominated ??= new List<(Microsoft.CodeAnalysis.Text.TextSpan, int)>())
                        .Add((DominatingRegion(reference.Parent!).Syntax.Span, reference.Syntax.Span.End));
                    continue; // may not have run on the path that transferred: keep scanning
                default:
                    Report(context, rhsUse ?? reference, variable);
                    return; // one report per transfer keeps the noise proportional
            }
        }
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
                && parent.Syntax.Span.Contains(transferPosition)
                && !child.Syntax.Span.Contains(transferPosition)
                && !dominatingPart.Syntax.Span.Contains(transferPosition))
            {
                return true;
            }
        }

        return false;
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
                .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable));
            return rhsUse is not null ? ReferenceRole.Use : ReinitializationRole(assignment, transferPosition, scope);
        }

        if (reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out } outArgument)
        {
            rhsUse = SiblingArgumentRead(outArgument, variable);
            return rhsUse is not null ? ReferenceRole.Use : ReinitializationRole(outArgument, transferPosition, scope);
        }

        // (list, _) = (...): a deconstruction target is definitely assigned like a
        // simple-assignment target, with the same RHS-read caveat.
        if (DeconstructionOf(reference) is { } deconstruction)
        {
            rhsUse = deconstruction.Value.DescendantsAndSelf()
                .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable));
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

    private static IOperation? SiblingArgumentRead(IArgumentOperation outArgument, ISymbol variable)
    {
        var arguments = outArgument.Parent switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => ImmutableArray<IArgumentOperation>.Empty,
        };

        return arguments.Where(argument => !ReferenceEquals(argument, outArgument))
            .SelectMany(argument => argument.Value.DescendantsAndSelf())
            .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable));
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
            ? ReferenceRole.FreshValueFromHere
            : ReferenceRole.ConditionalReinitialization;
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

            if (!child.Syntax.Span.Contains(transferPosition))
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
