using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// Shared plumbing for the rules that inspect computation lambdas (MZR002, MZR003): finding the
// lambdas among a factory invocation's arguments, walking their bodies, and locating where to
// report.
internal static class ComputationLambdas
{
    // Every anonymous function passed to the invocation -- directly, through a conversion, or as
    // an element of the params array the structured-concurrency factories take.
    public static IEnumerable<IAnonymousFunctionOperation> OfInvocation(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            foreach (var lambda in LambdasIn(argument.Value))
            {
                yield return lambda;
            }
        }
    }

    private static IEnumerable<IAnonymousFunctionOperation> LambdasIn(IOperation value)
    {
        switch (value)
        {
            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda }:
                yield return lambda;
                break;
            case IConversionOperation conversion:
                foreach (var lambda in LambdasIn(conversion.Operand))
                {
                    yield return lambda;
                }

                break;
            case IArrayCreationOperation { Initializer: { } initializer }:
                foreach (var element in initializer.ElementValues)
                {
                    foreach (var lambda in LambdasIn(element))
                    {
                        yield return lambda;
                    }
                }

                break;
        }
    }

    // Depth-first walk of a computation body that does NOT descend into a nested factory call's
    // subtree: that nested invocation triggers its own analysis (the operation action fires per
    // invocation regardless of nesting), so descending here would double-report its lambdas.
    public static IEnumerable<IOperation> Descend(IOperation root)
    {
        foreach (var child in root.ChildOperations)
        {
            yield return child;

            if (child is IInvocationOperation invocation && FactoryMethods.IsComputationHost(invocation.TargetMethod))
            {
                continue;
            }

            foreach (var descendant in Descend(child))
            {
                yield return descendant;
            }
        }
    }

    // The tightest useful squiggle for an invocation: the member name, not the whole call with
    // its (possibly multi-line lambda) arguments.
    public static Location NameLocation(IInvocationOperation invocation)
    {
        if (invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess })
        {
            return memberAccess.Name.GetLocation();
        }

        return invocation.Syntax.GetLocation();
    }
}
