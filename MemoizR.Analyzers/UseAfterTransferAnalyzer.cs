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

            if (!isTransfer || ReferencedVariable(argument) is not { } variable)
            {
                continue;
            }

            yield return (variable, operation.Syntax.Span.End, EnclosingFunctionBody(operation, block));
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

    private static void ReportUsesAfter(OperationBlockAnalysisContext context, IOperation scope, ISymbol variable, int transferPosition)
    {
        // Source-ordered walk of every later reference: a reference that is the TARGET of a
        // simple assignment re-initializes the variable, so it and everything after it is a new
        // value -- stop there.
        var laterReferences = scope.DescendantsAndSelf()
            .Where(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                && operation.Syntax.SpanStart >= transferPosition)
            .OrderBy(operation => operation.Syntax.SpanStart);

        foreach (var reference in laterReferences)
        {
            switch (Classify(reference, variable, transferPosition, out var rhsUse))
            {
                case ReferenceRole.FreshValueFromHere:
                    return; // a definite reinitialization: everything after is a new value
                case ReferenceRole.ConditionalReinitialization:
                    continue; // may not have run on the path that transferred: keep scanning
                default:
                    Report(context, rhsUse ?? reference, variable);
                    return; // one report per transfer keeps the noise proportional
            }
        }
    }

    private enum ReferenceRole { Use, FreshValueFromHere, ConditionalReinitialization }

    // A reference that REINITIALIZES the variable ends tracking when it definitely executes
    // and is skipped when conditional (sibling arms may not run on the path that transferred).
    // Reinitializations are a simple-assignment target -- unless its RHS itself READS the
    // transferred value (`list = Clone(list)` builds the replacement from it: that read is the
    // use to report) -- and an `out` argument, which the callee must assign and cannot read.
    private static ReferenceRole Classify(IOperation reference, ISymbol variable, int transferPosition, out IOperation? rhsUse)
    {
        rhsUse = null;

        if (reference.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, reference))
        {
            rhsUse = assignment.Value.DescendantsAndSelf()
                .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable));
            return rhsUse is not null ? ReferenceRole.Use : ReinitializationRole(assignment, transferPosition);
        }

        if (reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out } outArgument)
        {
            return ReinitializationRole(outArgument, transferPosition);
        }

        return ReferenceRole.Use;
    }

    private static ReferenceRole ReinitializationRole(IOperation reinitialization, int transferPosition)
    {
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
