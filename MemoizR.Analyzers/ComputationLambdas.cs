using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
            foreach (var body in OfArgumentValue(argument.Value, invocation.SemanticModel))
            {
                yield return body;
            }
        }
    }

    // A single argument's computations, for callers that need per-argument resolution (MZR004
    // distinguishes "resolved to nothing" from a walked body).
    public static IEnumerable<ComputationBody> OfArgumentValue(IOperation value, SemanticModel? semanticModel)
    {
        return BodiesIn(value, semanticModel, visitedVariables: null);
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
            case IMethodReferenceOperation bareMethodReference:
                // Like the bare-lambda case: GetOperation on a variable initializer's method
                // group yields the reference without the delegate-creation wrapper.
                if (ResolveMethodBody(bareMethodReference.Method, semanticModel) is { } resolvedBare)
                {
                    yield return resolvedBare;
                }

                break;
            case IConversionOperation conversion:
                foreach (var body in BodiesIn(conversion.Operand, semanticModel, visitedVariables))
                {
                    yield return body;
                }

                break;
            case IArrayCreationOperation { Initializer: { } initializer }:
                foreach (var body in BodiesInArrayElements(initializer, semanticModel, visitedVariables))
                {
                    yield return body;
                }

                break;
            case ILocalReferenceOperation or IFieldReferenceOperation or IPropertyReferenceOperation:
                foreach (var body in BodiesFromVariableInitializer(ReferencedVariable(value), semanticModel, visitedVariables))
                {
                    yield return body;
                }

                break;
        }
    }

    // The params array of computations the structured-concurrency factories take.
    private static IEnumerable<ComputationBody> BodiesInArrayElements(IArrayInitializerOperation initializer, SemanticModel? semanticModel, HashSet<ISymbol>? visitedVariables)
    {
        foreach (var element in initializer.ElementValues)
        {
            foreach (var body in BodiesIn(element, semanticModel, visitedVariables))
            {
                yield return body;
            }
        }
    }

    // Shared with MZR004's method-group receiver resolution.
    public static ISymbol? ReferencedVariable(IOperation reference)
    {
        return reference switch
        {
            ILocalReferenceOperation local => local.Local,
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };
    }

    // `Func<Task<int>> compute = async () => ...; f.CreateMemoizR(compute);` is the same
    // computation as the inline form, reached through a variable. Best-effort resolution: the
    // variable's same-tree INITIALIZER (a later reassignment is dataflow the analyzer does not
    // chase -- the runtime checks cover what this cannot see, like the method-group rule
    // above). The visited set breaks initializer reference cycles (two fields initialized from
    // each other).
    private static IEnumerable<ComputationBody> BodiesFromVariableInitializer(ISymbol? variable, SemanticModel? semanticModel, HashSet<ISymbol>? visitedVariables)
    {
        visitedVariables ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (variable is null || !visitedVariables.Add(variable))
        {
            yield break;
        }

        var operation = SameTreeInitializerOperation(variable, semanticModel);
        if (operation is null)
        {
            yield break;
        }

        foreach (var body in BodiesIn(operation, semanticModel, visitedVariables))
        {
            yield return body;
        }
    }

    // The variable's same-tree INITIALIZER as an operation. Locals and fields declare through
    // VariableDeclaratorSyntax; auto-properties with an initializer (`Func<...> Compute { get; }
    // = async () => ...`) through PropertyDeclarationSyntax. Computed properties have no
    // initializer and stay unresolvable, as they should. Shared with MZR004's method-group
    // receiver resolution.
    public static IOperation? SameTreeInitializerOperation(ISymbol? variable, SemanticModel? semanticModel)
    {
        if (variable is null || semanticModel is null)
        {
            return null;
        }

        var declaration = variable.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null || declaration.SyntaxTree != semanticModel.SyntaxTree)
        {
            return null;
        }

        var initializer = declaration.GetSyntax() switch
        {
            VariableDeclaratorSyntax { Initializer.Value: { } value } => value,
            PropertyDeclarationSyntax { Initializer.Value: { } value } => value,
            // `var (patch, _) = (...)` declares through a designation; its initializer is the
            // positionally matching element of the right-hand tuple.
            SingleVariableDesignationSyntax designation => DeconstructionInitializer(designation),
            _ => null,
        };

        return initializer is null ? null : semanticModel.GetOperation(initializer);
    }

    private static ExpressionSyntax? DeconstructionInitializer(SingleVariableDesignationSyntax designation)
    {
        var indexes = new List<int>();
        SyntaxNode current = designation;
        while (current.Parent is ParenthesizedVariableDesignationSyntax parent)
        {
            indexes.Add(parent.Variables.IndexOf((VariableDesignationSyntax)current));
            current = parent;
        }

        if (current.Parent is not DeclarationExpressionSyntax declaration
            || declaration.Parent is not AssignmentExpressionSyntax { Right: { } right })
        {
            return null;
        }

        // Indexes were recorded innermost-first; apply them outermost-first over the (possibly
        // nested) tuple literal. A non-literal right-hand side stays unresolvable.
        for (var i = indexes.Count - 1; i >= 0; i--)
        {
            if (right is not TupleExpressionSyntax tuple || indexes[i] >= tuple.Arguments.Count)
            {
                return null;
            }

            right = tuple.Arguments[indexes[i]].Expression;
        }

        return right;
    }

    // A ref-local ALIAS writes its referent: `ref var alias = ref patch; alias = ...` rebinds
    // patch just as directly. The alias chain resolves through `= ref` initializers; the
    // visited set breaks alias cycles. Shared by MZR004's delegate-reassignment scan and the
    // provenance checks in ReceiverChains.
    public static bool WritesVariable(ExpressionSyntax target, ISymbol variable, SemanticModel semanticModel)
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

    // A method group (`f.CreateMemoizR(Compute)`) or local-function reference is as much a
    // computation as a lambda; its declaration is the body to analyze. Resolution is same-tree
    // by design: another tree's declarations have no operation model here, and the runtime
    // checks still cover what the analyzer cannot see. Shared with MZR004's helper chasing.
    public static ComputationBody? ResolveMethodBody(IMethodSymbol method, SemanticModel? semanticModel)
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

    // "Captured" = declared outside the computation's declaring syntax (the lambda expression,
    // or the method/local-function declaration for a method-group computation). The span test
    // (rather than comparing containing symbols) is what keeps nested non-computation lambdas
    // correct: a local declared in a nested LINQ lambda belongs to the computation and must not
    // be treated as captured, while a local of the enclosing method must be. Shared by MZR002
    // (captured writes) and MZR004 (non-Sendable captures).
    public static bool IsDeclaredOutside(ISymbol symbol, SyntaxNode scope)
    {
        var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null)
        {
            // Compiler-generated (e.g. a setter's value parameter): nothing actionable to point at.
            return false;
        }

        return declaration.SyntaxTree != scope.SyntaxTree
            || !scope.Span.Contains(declaration.Span);
    }

    // A symbol or member referenced only inside nameof() is a compile-time string -- nothing
    // is captured, read, invoked, or stored. Shared guard for MZR002's and MZR004's reporting
    // and helper chasing.
    public static bool IsInsideNameOf(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is INameOfOperation)
            {
                return true;
            }
        }

        return false;
    }

    // Whether the symbol's DECLARING FUNCTION (method, local function, lambda, accessor)
    // lexically encloses the scope -- i.e. the symbol lives in an environment the scope's
    // closure shares, rather than in some helper invocation that is recreated per call.
    // Shared by MZR002's and MZR004's local-function chasing.
    public static bool DeclaredInFunctionEnclosing(ISymbol symbol, SyntaxNode scope)
    {
        var declaration = symbol.ContainingSymbol?.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null)
        {
            // A compiler-synthesized container -- the top-level-statements entry point -- has
            // no declaration of its own; its locals are the file's global statements, which
            // enclose everything in the file.
            return symbol.DeclaringSyntaxReferences.FirstOrDefault() is { } own
                && own.SyntaxTree == scope.SyntaxTree;
        }

        return declaration.SyntaxTree == scope.SyntaxTree
            && declaration.Span.Contains(scope.Span);
    }

    // The expressions a node writes: an assignment's left-hand side (deconstruction tuples
    // flattened, nesting included -- `(patch, _) = ...` writes `patch`) or a ref/out argument
    // (`in` is excluded: a readonly reference cannot rebind the variable). Shared by MZR004's
    // delegate-reassignment scan and the provenance checks in ReceiverChains.
    public static IEnumerable<ExpressionSyntax>? ReassignmentTargets(SyntaxNode node)
    {
        return node switch
        {
            AssignmentExpressionSyntax assignment => FlattenTargets(assignment.Left),
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
    // it counts. In the same function, one textually before the reference obviously precedes
    // it, and one textually after still reaches it when a loop encloses both AND the variable
    // outlives the iteration (a loop-body local is freshly initialized each pass). Shared by
    // MZR004's delegate-reassignment scan and the provenance checks in ReceiverChains.
    public static bool CanExecuteBefore(SyntaxNode assignment, SyntaxNode reference, ISymbol variable)
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
            if (current is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                && current.Span.Contains(reference.Span)
                && !DeclaredWithin(variable, current))
            {
                return true;
            }
        }

        return false;
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
