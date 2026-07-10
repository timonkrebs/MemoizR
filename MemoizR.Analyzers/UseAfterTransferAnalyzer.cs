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
// value. Deliberately a heuristic (source order approximates execution order; loop back-edges
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
            foreach (var (variable, transferPosition) in Transfers(block))
            {
                ReportUsesAfter(context, block, variable, transferPosition);
            }
        }
    }

    // Every Sending<T> creation (constructor or Sending.Transfer) whose argument is a
    // local/parameter reference, with the position the transfer happens at.
    private static IEnumerable<(ISymbol Variable, int Position)> Transfers(IOperation block)
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

            yield return (variable, operation.Syntax.Span.End);
        }
    }

    private static void ReportUsesAfter(OperationBlockAnalysisContext context, IOperation block, ISymbol variable, int transferPosition)
    {
        // Source-ordered walk of every later reference: a reference that is the TARGET of a
        // simple assignment re-initializes the variable, so it and everything after it is a new
        // value -- stop there.
        var laterReferences = block.DescendantsAndSelf()
            .Where(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable)
                && operation.Syntax.SpanStart >= transferPosition)
            .OrderBy(operation => operation.Syntax.SpanStart);

        foreach (var reference in laterReferences)
        {
            if (reference.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, reference))
            {
                // A reassignment gives the variable a fresh value -- but only if its RHS does
                // not itself READ the transferred one (`list = Clone(list)` uses it to build
                // the replacement, which is exactly a use after transfer).
                var rhsUse = assignment.Value.DescendantsAndSelf()
                    .FirstOrDefault(operation => SymbolEqualityComparer.Default.Equals(ReferencedVariable(operation), variable));
                if (rhsUse is not null)
                {
                    Report(context, rhsUse, variable);
                    return;
                }

                // ...and only if it DEFINITELY executes: a reassignment inside a branch the
                // transfer is not part of (`if (reset) list = new();`) leaves the other path
                // still using the transferred value, so the scan continues past it (without
                // flagging the write target itself).
                if (IsOnTheTransfersConditionalLevel(assignment, transferPosition))
                {
                    return;
                }

                continue;
            }

            Report(context, reference, variable);
            return; // one report per transfer keeps the noise proportional
        }
    }

    // True when the reassignment DEFINITELY executes on the path that transferred: walking up
    // from the assignment, every branching ancestor (if/switch/loop/try/?.) must be entered
    // through the ARM that also contains the transfer. An ancestor merely spanning the
    // transfer is not enough -- `try { Transfer(list); } catch { list = new(); }` spans it,
    // but the catch arm may never run. Checking the path-CHILD's span covers both cases at
    // once (a child containing the transfer implies its parent does too); the one arm exempted
    // is a finally block, which always executes.
    private static bool IsOnTheTransfersConditionalLevel(IOperation assignment, int transferPosition)
    {
        for (IOperation child = assignment; child.Parent is { } parent; child = parent)
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
