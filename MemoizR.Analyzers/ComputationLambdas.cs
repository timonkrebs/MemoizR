using System;
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

    // Every computation passed to the invocation -- directly, through a conversion, as an
    // element of the params array the structured-concurrency factories take, or through a
    // delegate variable whose (same-tree) initializer holds the computation.
    public static IEnumerable<ComputationBody> OfInvocation(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            foreach (var body in BodiesIn(argument.Value, invocation.SemanticModel, visitedVariables: null))
            {
                yield return body;
            }
        }
    }

    private static IEnumerable<ComputationBody> BodiesIn(IOperation value, SemanticModel? semanticModel, HashSet<ISymbol>? visitedVariables)
    {
        switch (value)
        {
            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda }:
                yield return new ComputationBody(lambda.Body, lambda.Syntax);
                break;
            case IAnonymousFunctionOperation bareLambda:
                // GetOperation on a variable initializer's lambda syntax yields the function
                // operation itself, without the enclosing delegate-creation wrapper.
                yield return new ComputationBody(bareLambda.Body, bareLambda.Syntax);
                break;
            case IDelegateCreationOperation { Target: IMethodReferenceOperation methodReference }:
                if (ResolveMethodBody(methodReference.Method, semanticModel) is { } resolved)
                {
                    yield return resolved;
                }

                break;
            case IConversionOperation conversion:
                foreach (var body in BodiesIn(conversion.Operand, semanticModel, visitedVariables))
                {
                    yield return body;
                }

                break;
            case IArrayCreationOperation { Initializer: { } initializer }:
                foreach (var element in initializer.ElementValues)
                {
                    foreach (var body in BodiesIn(element, semanticModel, visitedVariables))
                    {
                        yield return body;
                    }
                }

                break;
            case ILocalReferenceOperation localReference:
                foreach (var body in BodiesFromVariableInitializer(localReference.Local, semanticModel, visitedVariables))
                {
                    yield return body;
                }

                break;
            case IFieldReferenceOperation fieldReference:
                foreach (var body in BodiesFromVariableInitializer(fieldReference.Field, semanticModel, visitedVariables))
                {
                    yield return body;
                }

                break;
        }
    }

    // `Func<Task<int>> compute = async () => ...; f.CreateMemoizR(compute);` is the same
    // computation as the inline form, reached through a variable. Best-effort resolution: the
    // variable's same-tree INITIALIZER (a later reassignment is dataflow the analyzer does not
    // chase -- the runtime checks cover what this cannot see, like the method-group rule
    // above). The visited set breaks initializer reference cycles (two fields initialized from
    // each other).
    private static IEnumerable<ComputationBody> BodiesFromVariableInitializer(ISymbol variable, SemanticModel? semanticModel, HashSet<ISymbol>? visitedVariables)
    {
        visitedVariables ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (semanticModel is null || !visitedVariables.Add(variable))
        {
            yield break;
        }

        var declaration = variable.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null
            || declaration.SyntaxTree != semanticModel.SyntaxTree
            || declaration.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initializer })
        {
            yield break;
        }

        var operation = semanticModel.GetOperation(initializer);
        if (operation is null)
        {
            yield break;
        }

        foreach (var body in BodiesIn(operation, semanticModel, visitedVariables))
        {
            yield return body;
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
    // COMPUTATION bodies: that nested invocation triggers its own analysis (the operation action
    // fires per invocation regardless of nesting), so descending into them here would
    // double-report. The nested call's ORDINARY arguments still belong to the outer computation
    // -- a `++counter` in a label argument runs during the outer evaluation -- so those are
    // walked.
    public static IEnumerable<IOperation> Descend(IOperation root)
    {
        foreach (var child in root.ChildOperations)
        {
            yield return child;

            if (child is IInvocationOperation invocation && FactoryMethods.IsComputationHost(invocation.TargetMethod))
            {
                foreach (var descendant in DescendNestedHostArguments(invocation, Descend))
                {
                    yield return descendant;
                }

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
                foreach (var descendant in DescendNestedHostArguments(invocation, DescendDirectExecution))
                {
                    yield return descendant;
                }

                continue;
            }

            foreach (var descendant in DescendDirectExecution(child))
            {
                yield return descendant;
            }
        }
    }

    // Walks a nested computation host's ORDINARY arguments with the caller's walker (they are
    // evaluated as part of the outer computation), skipping the delegate arguments, whose
    // bodies the nested invocation's own analyzer pass covers.
    private static IEnumerable<IOperation> DescendNestedHostArguments(IInvocationOperation nestedHost, Func<IOperation, IEnumerable<IOperation>> walker)
    {
        foreach (var argument in nestedHost.Arguments)
        {
            if (IsComputationDelegateArgument(argument.Value))
            {
                continue;
            }

            yield return argument.Value;

            foreach (var descendant in walker(argument.Value))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsComputationDelegateArgument(IOperation value)
    {
        return value switch
        {
            IDelegateCreationOperation => true,
            IConversionOperation conversion => IsComputationDelegateArgument(conversion.Operand),
            IArrayCreationOperation => true, // the params array of computations the structured factories take
            _ => false,
        };
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
