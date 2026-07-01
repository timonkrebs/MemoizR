using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// Shared plumbing for the rules that inspect computation bodies (MZR002, MZR003): finding the
// computations among a factory invocation's arguments -- anonymous functions, and method groups /
// local functions declared in the same file -- walking their bodies, and locating where to
// report.
internal static class ComputationLambdas
{
    // A computation's executable body plus the syntax that DECLARES the computation: the scope
    // against which "captured" is decided (a symbol declared outside it is state the computation
    // shares with other code).
    public readonly struct ComputationBody
    {
        public ComputationBody(IOperation body, SyntaxNode scope)
        {
            Body = body;
            Scope = scope;
        }

        public IOperation Body { get; }

        public SyntaxNode Scope { get; }
    }

    // Every computation passed to the invocation -- directly, through a conversion, or as an
    // element of the params array the structured-concurrency factories take.
    public static IEnumerable<ComputationBody> OfInvocation(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            foreach (var body in BodiesIn(argument.Value, invocation.SemanticModel))
            {
                yield return body;
            }
        }
    }

    private static IEnumerable<ComputationBody> BodiesIn(IOperation value, SemanticModel? semanticModel)
    {
        switch (value)
        {
            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda }:
                yield return new ComputationBody(lambda.Body, lambda.Syntax);
                break;
            case IDelegateCreationOperation { Target: IMethodReferenceOperation methodReference }:
                if (ResolveMethodBody(methodReference.Method, semanticModel) is { } resolved)
                {
                    yield return resolved;
                }

                break;
            case IConversionOperation conversion:
                foreach (var body in BodiesIn(conversion.Operand, semanticModel))
                {
                    yield return body;
                }

                break;
            case IArrayCreationOperation { Initializer: { } initializer }:
                foreach (var element in initializer.ElementValues)
                {
                    foreach (var body in BodiesIn(element, semanticModel))
                    {
                        yield return body;
                    }
                }

                break;
        }
    }

    // A method group (`f.CreateMemoizR(Compute)`) or local-function reference is as much a
    // computation as a lambda; its declaration is the body to analyze. Resolution is same-tree
    // by design: another tree's declarations have no operation model here, and the runtime
    // checks still cover what the analyzer cannot see.
    private static ComputationBody? ResolveMethodBody(IMethodSymbol method, SemanticModel? semanticModel)
    {
        if (semanticModel is null)
        {
            return null;
        }

        var declaration = (method.PartialImplementationPart ?? method).DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null || declaration.SyntaxTree != semanticModel.SyntaxTree)
        {
            return null;
        }

        var syntax = declaration.GetSyntax();
        var operation = semanticModel.GetOperation(syntax);
        return operation is null ? null : new ComputationBody(operation, syntax);
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

    // Like Descend, but restricted to the operations the computation executes as part of its OWN
    // evaluation: nested anonymous functions and local-function declarations are pruned. Their
    // bodies run only if and when the delegate is invoked -- and MZR003's own fix guidance is to
    // BUILD such a callback ("schedule the write outside the evaluation"), which executes later
    // on a flow that holds no evaluation lock and so must not be flagged. The cost is a false
    // negative for a nested function invoked synchronously inside the computation; the runtime
    // exception still guards that path. MZR002 deliberately keeps the full walk: a captured-state
    // write is a data race whenever the callback runs, deferred or not.
    public static IEnumerable<IOperation> DescendDirectExecution(IOperation root)
    {
        foreach (var child in root.ChildOperations)
        {
            if (child is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                continue;
            }

            yield return child;

            if (child is IInvocationOperation invocation && FactoryMethods.IsComputationHost(invocation.TargetMethod))
            {
                continue;
            }

            foreach (var descendant in DescendDirectExecution(child))
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
