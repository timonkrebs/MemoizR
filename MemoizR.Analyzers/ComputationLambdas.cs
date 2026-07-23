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
        // GetOperation on a variable initializer's lambda/method-group syntax yields the
        // operation WITHOUT the enclosing delegate-creation wrapper: unwrapping here makes
        // the wrapped and bare forms one case each.
        if (value is IDelegateCreationOperation creation)
        {
            value = creation.Target;
        }

        switch (value)
        {
            case IAnonymousFunctionOperation lambda:
                yield return new ComputationBody(lambda.Body, lambda.Syntax);
                break;
            case IMethodReferenceOperation methodReference:
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

            // A conditional (or null-coalescing) computation stores whichever arm the flow
            // picks: both are possible bodies, so both are walked.
            case IConditionalOperation or ICoalesceOperation:
                foreach (var body in BranchBodies(value, semanticModel, visitedVariables))
                {
                    yield return body;
                }

                break;
        }
    }

    private static IEnumerable<ComputationBody> BranchBodies(IOperation value, SemanticModel? semanticModel, HashSet<ISymbol>? visitedVariables)
    {
        var arms = value switch
        {
            IConditionalOperation conditional => (First: (IOperation?)conditional.WhenTrue, Second: conditional.WhenFalse),
            ICoalesceOperation coalesce => (First: (IOperation?)coalesce.Value, Second: coalesce.WhenNull),
            _ => (First: null, Second: null),
        };

        if (arms.First is null)
        {
            yield break;
        }

        foreach (var body in BodiesIn(arms.First, semanticModel, visitedVariables))
        {
            yield return body;
        }

        if (arms.Second is null)
        {
            yield break;
        }

        foreach (var body in BodiesIn(arms.Second, semanticModel, visitedVariables))
        {
            yield return body;
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

        // `Func<int,int> patch; patch = static x => x;` initializes by assignment: when the
        // SOLE same-tree write is a simple assignment, its right-hand side is the initializer
        // -- for a deconstruction form, the positionally matching tuple element.
        if (initializer is null && EffectiveInitializerAssignment(variable, semanticModel) is { } assignment)
        {
            initializer = AssignedElementFor(assignment.Left, assignment.Right, variable, semanticModel);
        }

        return initializer is null ? null : semanticModel.GetOperation(initializer);
    }

    // The right-hand expression a simple assignment gives THIS variable: the whole right side
    // for `x = value`, the positionally matching element for `(x, _) = (value, 0)` (nesting
    // included). A non-literal tuple right side stays unresolvable, like the declaration
    // deconstruction above.
    private static ExpressionSyntax? AssignedElementFor(ExpressionSyntax left, ExpressionSyntax right, ISymbol variable, SemanticModel semanticModel)
    {
        if (left is not TupleExpressionSyntax leftTuple)
        {
            return WritesVariable(left, variable, semanticModel) ? right : null;
        }

        if (right is not TupleExpressionSyntax rightTuple || leftTuple.Arguments.Count != rightTuple.Arguments.Count)
        {
            return null;
        }

        for (var i = 0; i < leftTuple.Arguments.Count; i++)
        {
            if (AssignedElementFor(leftTuple.Arguments[i].Expression, rightTuple.Arguments[i].Expression, variable, semanticModel) is { } element)
            {
                return element;
            }
        }

        return null;
    }

    // The effective initializer narrowed to the shapes whose right-hand side resolves to a
    // single operation; an out-argument handoff has no RHS here (its bodies come from the
    // callee's assignments, which MZR004 resolves itself).
    public static AssignmentExpressionSyntax? EffectiveInitializerAssignment(ISymbol variable, SemanticModel? semanticModel, SyntaxNode? readSite = null)
    {
        return EffectiveInitializerWrite(variable, semanticModel, readSite) as AssignmentExpressionSyntax;
    }

    // The single same-tree WRITE standing in for a missing declaration initializer -- a
    // plain `x = value` (or simple deconstruction), or an OUT-argument handoff
    // (`Provide(out patch)`, which the language requires to assign) -- or null when the
    // declaration has an initializer, there are several writes (ambiguous), or the one
    // write does not fully determine the value. Reassignment scans EXCLUDE this node: it is
    // the initialization, not a rebind.
    public static SyntaxNode? EffectiveInitializerWrite(ISymbol variable, SemanticModel? semanticModel, SyntaxNode? readSite = null)
    {
        if (semanticModel is null || variable.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                is VariableDeclaratorSyntax { Initializer: not null }
                or PropertyDeclarationSyntax { Initializer: not null }
                or SingleVariableDesignationSyntax)
        {
            return null;
        }

        SyntaxNode? sole = null;
        foreach (var node in semanticModel.SyntaxTree.GetRoot().DescendantNodes())
        {
            // WritesVariable, not plain symbol equality: the reassignment scans count a
            // write through a ref alias (`ref var alias = ref patch; alias = ...`), so the
            // initializer detection must recognize the same write or the sole initializing
            // assignment would read as a rebind. With a READ SITE, writes that cannot run
            // before it are ignored outright: a future rebind neither initializes nor
            // disqualifies the write whose value this read observes.
            if (ReassignmentTargets(node) is not { } targets
                || (readSite is not null && !CanExecuteBefore(node, readSite, variable, semanticModel)))
            {
                continue;
            }

            var target = targets.FirstOrDefault(candidate => WritesVariable(candidate, variable, semanticModel));
            if (target is null)
            {
                continue;
            }

            if (sole is not null || !WritesThroughOwnReceiver(target, variable) || !IsInitializingWriteShape(node))
            {
                return null;
            }

            sole = node;
        }

        return sole is not null && DominatesItsFunction(sole, variable) && ReachesMemberRead(sole, variable, readSite)
            ? sole
            : null;
    }

    // For MEMBER storage the sole write must also provably reach THIS read: unlike locals
    // (definite assignment) and parameters (binding), a field holds its default or an
    // externally supplied value until the write actually runs -- a write in some unrelated
    // method that merely COULD run first proves nothing. Provable = the write shares the
    // read's function, or its function lexically contains the read (the flow reaching a
    // computation built after the write passed it first).
    private static bool ReachesMemberRead(SyntaxNode write, ISymbol variable, SyntaxNode? readSite)
    {
        if (variable is not (IFieldSymbol or IPropertySymbol))
        {
            return true;
        }

        if (readSite is null)
        {
            return false;
        }

        var function = EnclosingFunction(write);
        return function is not null
            && (ReferenceEquals(function, EnclosingFunction(readSite)) || function.Span.Contains(readSite.Span));
    }

    // Only a write that fully DETERMINES the value qualifies: `x ??= ...` / `x += ...` can
    // leave (or combine with) an older value supplied elsewhere, and a `ref` argument may
    // never assign -- while an `out` argument must, and a simple deconstruction determines
    // the variable's slot (SameTreeInitializerOperation extracts the element).
    private static bool IsInitializingWriteShape(SyntaxNode node)
    {
        return node switch
        {
            AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression),
            ArgumentSyntax argument => argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword),
            _ => false,
        };
    }

    // For an instance member, only a write through the member's OWN object initializes what
    // a read observes -- and this synthesis is receiver-blind about the read, so only the
    // unambiguous shapes count: a bare identifier (implicit this, or a ref alias) or an
    // explicit `this.X`. A write on any OTHER receiver (`other.Patch = safe;`) says nothing
    // about `this.Patch`, so the whole story turns unprovable instead.
    private static bool WritesThroughOwnReceiver(ExpressionSyntax target, ISymbol variable)
    {
        if (variable is not (IFieldSymbol or IPropertySymbol) || variable.IsStatic)
        {
            return true;
        }

        return target is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
    }

    // A synthetic initializer must DOMINATE any read: storage that exists before the write
    // (a field, a property, a parameter) can still hold a value supplied elsewhere when an
    // assignment nested in a conditional never runs. Locals need no check -- definite
    // assignment already forbids reading them on a path that skipped the write. "Dominates"
    // = plain statements all the way up to the write's own function (the argument-list hop
    // covers the out-argument shape; a conditional-access call stays rejected).
    private static bool DominatesItsFunction(SyntaxNode write, ISymbol variable)
    {
        if (variable is ILocalSymbol)
        {
            return true;
        }

        for (SyntaxNode? current = write.Parent; current is not null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
                or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or ArrowExpressionClauseSyntax
                or CompilationUnitSyntax)
            {
                return true;
            }

            if (current is not (BlockSyntax or ExpressionStatementSyntax or GlobalStatementSyntax
                or ArgumentListSyntax or InvocationExpressionSyntax))
            {
                return false;
            }
        }

        return true;
    }

    // Any same-tree write to the symbol that can execute before the read -- unlike the
    // reassignment checks, WITHOUT the effective-initializer excuse: used where the
    // declaration itself binds the value (a parameter, an aliased local), so every write is
    // a rebind. Unverifiable (no model) counts as written: callers hop or trust only on
    // proof.
    public static bool IsWrittenBefore(ISymbol variable, SyntaxNode reference, SemanticModel? semanticModel)
    {
        if (semanticModel is null)
        {
            return true;
        }

        foreach (var node in semanticModel.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (ReassignmentTargets(node) is { } targets
                && targets.Any(target => WritesVariable(target, variable, semanticModel))
                && CanExecuteBefore(node, reference, variable, semanticModel))
            {
                return true;
            }
        }

        return false;
    }

    // Call-site arguments substitute for a chased helper's parameters: maps are built
    // pre-substituted, so nested helper calls resolve through to the original computation's
    // operations. A property SETTER's implicit `value` parameter maps to the assignment's
    // right-hand side, and an INDEXER's index arguments map like call arguments (aliased
    // onto the accessors' own parameter symbols, which is what accessor bodies bind). The
    // OUTER bindings carry through -- a called local function closes over its enclosing
    // helper's parameters (`void Inner() => s.Set(2); Inner();`), so dropping them would
    // orphan those references; keys are parameter symbols, so unrelated callees cannot
    // collide, and a recursive call's fresh binding overwrites the stale one. Shared by
    // MZR003's Set-target provenance and MZR004's delegate chase.
    public static Dictionary<IParameterSymbol, IOperation>? BuildArgumentMap(IOperation operation, Dictionary<IParameterSymbol, IOperation>? outer)
    {
        var arguments = operation switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            IPropertyReferenceOperation propertyReference => propertyReference.Arguments,
            _ => default,
        };

        Dictionary<IParameterSymbol, IOperation>? map = null;
        if (!arguments.IsDefaultOrEmpty)
        {
            foreach (var argument in arguments)
            {
                if (argument.Parameter is { } parameter && SubstituteArguments(argument.Value, outer) is { } value)
                {
                    map ??= CopyOf(outer);
                    map[parameter] = value;
                    AliasAccessorParameters(operation, parameter, value, map);
                }
            }
        }

        if (SetterValue(operation, outer) is { } setter)
        {
            map ??= CopyOf(outer);
            map[setter.Parameter] = setter.Value;
        }

        return map ?? outer;
    }

    private static (IParameterSymbol Parameter, IOperation Value)? SetterValue(IOperation operation, Dictionary<IParameterSymbol, IOperation>? outer)
    {
        return operation is IPropertyReferenceOperation { Parent: ISimpleAssignmentOperation assignment } property
            && ReferenceEquals(assignment.Target, property)
            && property.Property.SetMethod?.Parameters.LastOrDefault() is { } valueParameter
            && SubstituteArguments(assignment.Value, outer) is { } value
            ? (valueParameter, value)
            : null;
    }

    // An accessor body binds the ACCESSOR's own parameter symbols, distinct from the
    // property parameters an indexer reference's arguments name: alias the ordinal twins on
    // the property and both accessors so a chased `set => s.Set(value)` resolves `s` to the
    // call-site index argument whichever symbol the body carries.
    private static void AliasAccessorParameters(IOperation operation, IParameterSymbol parameter, IOperation value, Dictionary<IParameterSymbol, IOperation> map)
    {
        if (operation is not IPropertyReferenceOperation { Property: { } property })
        {
            return;
        }

        foreach (var parameters in new[] { property.Parameters, property.GetMethod?.Parameters ?? default, property.SetMethod?.Parameters ?? default })
        {
            if (!parameters.IsDefaultOrEmpty && parameter.Ordinal < parameters.Length)
            {
                map[parameters[parameter.Ordinal]] = value;
            }
        }
    }

    // A stable VALUE identity for an argument map: chase guards key on it so the same
    // callee syntax re-walks under different bindings (two outer calls handing different
    // lambdas into one inner call site) while a recursive call -- whose rebuilt map carries
    // the same substituted values -- terminates. Parameters are identified by declaration
    // position and ordinal, values by their operation's syntax position: both stable within
    // one compilation, unlike symbol hash codes.
    public static string ArgumentMapKey(Dictionary<IParameterSymbol, IOperation>? map)
    {
        if (map is null || map.Count == 0)
        {
            return "";
        }

        var parts = new List<string>(map.Count);
        foreach (var entry in map)
        {
            var declaration = entry.Key.DeclaringSyntaxReferences.FirstOrDefault()
                ?? entry.Key.ContainingSymbol?.DeclaringSyntaxReferences.FirstOrDefault();
            var parameter = declaration is null
                ? $"{entry.Key.Name}"
                : $"{declaration.SyntaxTree.FilePath}:{declaration.Span.Start}";
            parts.Add($"{parameter}#{entry.Key.Ordinal}={entry.Value.Syntax.SyntaxTree.FilePath}:{entry.Value.Syntax.SpanStart}");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    private static Dictionary<IParameterSymbol, IOperation> CopyOf(Dictionary<IParameterSymbol, IOperation>? outer)
    {
        return outer is null
            ? new Dictionary<IParameterSymbol, IOperation>(SymbolEqualityComparer.Default)
            : new Dictionary<IParameterSymbol, IOperation>(outer, SymbolEqualityComparer.Default);
    }

    public static IOperation? SubstituteArguments(IOperation? reference, Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        var current = reference;
        while (current is IConversionOperation conversion)
        {
            current = conversion.Operand;
        }

        return current is IParameterReferenceOperation parameterReference
            && argumentMap?.TryGetValue(parameterReference.Parameter, out var argument) == true
            ? argument
            : reference;
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

    // The values a simple assignment gives THIS variable, deconstructions paired
    // positionally (`(other, later) = (a, b)` assigns only `b` to `later`). WritesVariable,
    // not plain symbol equality: an assignment through a ref alias rebinds the variable
    // too. Shared by MZR004's delegate chases and the assembled-patch resolution below.
    public static IEnumerable<IOperation> AssignedValuesFor(ExpressionSyntax left, ExpressionSyntax right, ISymbol variable, SemanticModel semanticModel)
    {
        if (left is TupleExpressionSyntax leftTuple && right is TupleExpressionSyntax rightTuple
            && leftTuple.Arguments.Count == rightTuple.Arguments.Count)
        {
            for (var i = 0; i < leftTuple.Arguments.Count; i++)
            {
                foreach (var value in AssignedValuesFor(leftTuple.Arguments[i].Expression, rightTuple.Arguments[i].Expression, variable, semanticModel))
                {
                    yield return value;
                }
            }

            yield break;
        }

        if (WritesVariable(left, variable, semanticModel)
            && semanticModel.GetOperation(right) is { } operation)
        {
            yield return operation;
        }
    }

    // Best-effort SUPPLEMENTAL bodies for an Apply patch argument beyond OfArgumentValue: a
    // patch assembled by a same-tree out-helper (the sole dominating out handoff, following
    // forwarded handoffs), or returned by a same-tree computed get-only delegate property.
    // Resolution only -- MZR004 owns the unverifiable accounting for these shapes; MZR003
    // needs the BODIES, because a Set inside them still throws under the evaluation lock.
    public static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> AssembledPatchBodies(IOperation value, SemanticModel? semanticModel)
    {
        var reference = value;
        while (reference is IConversionOperation conversion)
        {
            reference = conversion.Operand;
        }

        var variable = ReferencedVariable(reference);
        if (variable is not null && EffectiveInitializerWrite(variable, semanticModel, reference.Syntax) is ArgumentSyntax outWrite)
        {
            foreach (var body in OutHandoffBodies(outWrite, semanticModel, new HashSet<SyntaxNode>(), outerMap: null))
            {
                yield return body;
            }
        }

        // The INDEXER's argument map keeps the returned lambda's index-parameter references
        // resolvable (`this[Signal<int> s] { get { return x => { s.Set(1); ... }; } }`).
        if (variable is IPropertySymbol { GetMethod: { } getter, SetMethod: null }
            && ResolveMethodBody(getter, semanticModel) is { } getterBody)
        {
            var propertyMap = BuildArgumentMap(reference, null);
            foreach (var body in ReturnedBodies(getterBody, semanticModel, propertyMap))
            {
                yield return (body, propertyMap);
            }
        }

        // A patch produced by a same-tree delegate FACTORY -- called inline, or stored
        // through the variable's initializer -- resolves through the factory's returns,
        // with the call's own argument map.
        var call = reference as IInvocationOperation
            ?? UnwrappedInitializerCall(variable, semanticModel);
        if (call is not null && ResolveMethodBody(call.TargetMethod, semanticModel) is { } factoryBody)
        {
            var callMap = BuildArgumentMap(call, null);
            foreach (var body in ReturnedBodies(factoryBody, semanticModel, callMap))
            {
                yield return (body, callMap);
            }
        }
    }

    // A delegate VALUE collapsed through variable-alias initializers and parameter-to-
    // argument hops -- the resolution-only mirror of MZR004's invoked-delegate resolver,
    // with a strict rebind guard: a written alias or parameter stays put, because the
    // write (not the initializer or the call site) owns the value.
    public static IOperation ResolveDelegateValue(IOperation value, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        var current = value;
        HashSet<ISymbol>? visited = null;
        while (true)
        {
            var unwrapped = current;
            while (unwrapped is IConversionOperation conversion)
            {
                unwrapped = conversion.Operand;
            }

            if (unwrapped is IParameterReferenceOperation rebound
                && argumentMap?.ContainsKey(rebound.Parameter) == true
                && IsWrittenBefore(rebound.Parameter, current.Syntax, semanticModel))
            {
                return current;
            }

            if (SubstituteArguments(current, argumentMap) is { } substituted && !ReferenceEquals(substituted, current))
            {
                current = substituted;
                continue;
            }

            if (ReferencedVariable(unwrapped) is not { } variable)
            {
                return current;
            }

            visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var initializer = SameTreeInitializerOperation(variable, semanticModel);
            if (!visited.Add(variable) || initializer is null
                || IsWrittenBefore(variable, current.Syntax, semanticModel)
                || !IsReferenceShape(initializer))
            {
                return current;
            }

            current = initializer;
        }
    }

    private static bool IsReferenceShape(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation is IParameterReferenceOperation || ReferencedVariable(operation) is not null;
    }

    private static IInvocationOperation? UnwrappedInitializerCall(ISymbol? variable, SemanticModel? semanticModel)
    {
        var initializer = SameTreeInitializerOperation(variable, semanticModel);
        while (initializer is IConversionOperation conversion)
        {
            initializer = conversion.Operand;
        }

        return initializer as IInvocationOperation;
    }

    public static IEnumerable<ComputationBody> ReturnedBodies(ComputationBody methodBody, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap = null)
    {
        foreach (var inner in DescendDirectExecution(methodBody.Body))
        {
            if (inner is not IReturnOperation { ReturnedValue: { } returned })
            {
                continue;
            }

            // Aliases resolve too: `var q = p; return q;` hands back the call-site value.
            foreach (var body in OfArgumentValue(ResolveDelegateValue(returned, semanticModel, argumentMap), semanticModel))
            {
                yield return body;
            }
        }
    }

    // The delegate bodies an out-helper binds to its parameter: direct assignments'
    // paired values, plus FORWARDED out handoffs (`Provide(out d) { Build(out d); }`)
    // followed recursively -- the visited set bounds forwarding cycles. Each body carries
    // the CALL-SITE argument map (composed through forwarding), so a lambda using another
    // helper parameter keeps its provenance.
    public static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> OutHandoffBodies(
        ArgumentSyntax argument,
        SemanticModel? semanticModel,
        HashSet<SyntaxNode> visited,
        Dictionary<IParameterSymbol, IOperation>? outerMap)
    {
        if (semanticModel is null || !visited.Add(argument)
            || (semanticModel.GetOperation(argument) as IArgumentOperation) is not { Parameter: { } parameter } argumentOperation
            || parameter.ContainingSymbol is not IMethodSymbol method
            || ResolveMethodBody(method, semanticModel) is not { } helper)
        {
            yield break;
        }

        var callMap = argumentOperation.Parent is { } call
            ? BuildArgumentMap(call, outerMap)
            : outerMap;

        // The kill filter mirrors MZR004's accounting chase: an assignment definitely
        // overwritten on the straight-line path to the helper's return can never be the
        // delegate the caller receives.
        var writes = new List<SyntaxNode>();
        foreach (var node in helper.Scope.DescendantNodes())
        {
            if (node is AssignmentExpressionSyntax candidate
                && ReassignmentTargets(candidate) is { } targets
                && targets.Any(target => WritesVariable(target, parameter, semanticModel)))
            {
                writes.Add(candidate);
            }
        }

        foreach (var node in helper.Scope.DescendantNodes())
        {
            if (node is AssignmentExpressionSyntax victim
                && writes.Any(other => other != victim && DefinitelyOverwrites(other, victim, helper.Scope, parameter, semanticModel)))
            {
                continue;
            }

            foreach (var body in OutHandoffNodeBodies(node, parameter, semanticModel, visited, callMap))
            {
                yield return body;
            }
        }
    }

    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> OutHandoffNodeBodies(
        SyntaxNode node,
        IParameterSymbol parameter,
        SemanticModel semanticModel,
        HashSet<SyntaxNode> visited,
        Dictionary<IParameterSymbol, IOperation>? callMap)
    {
        switch (node)
        {
            case AssignmentExpressionSyntax assignment
                when ReassignmentTargets(assignment) is { } targets
                    && targets.Any(target => WritesVariable(target, parameter, semanticModel)):
                foreach (var value in AssignedValuesFor(assignment.Left, assignment.Right, parameter, semanticModel))
                {
                    // The call-site map resolves a value forwarded from ANOTHER delegate
                    // parameter (`Provide(source, out patch)` with `p = source`), aliases
                    // included.
                    foreach (var body in OfArgumentValue(ResolveDelegateValue(value, semanticModel, callMap), semanticModel))
                    {
                        yield return (body, callMap);
                    }
                }

                break;
            case ArgumentSyntax forwarded
                when forwarded.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)
                    && SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(forwarded.Expression).Symbol, parameter):
                foreach (var body in OutHandoffBodies(forwarded, semanticModel, visited, callMap))
                {
                    yield return body;
                }

                break;
        }
    }

    // "Straight-line" = the killer sits in plain statements between itself and wherever the
    // value is observed (an invoke site, or an out-helper's own scope end); a killer inside
    // an if/loop may not run, so it kills nothing -- and an exit statement between the two
    // writes can leave the earlier value observable, so nothing is definite past one. The
    // argument-list hop covers out-handoff killers; a conditional-access call stays rejected.
    public static bool DefinitelyOverwrites(SyntaxNode killer, SyntaxNode victim, SyntaxNode reference, ISymbol variable, SemanticModel semanticModel)
    {
        if (!IsDeterminingWrite(killer, variable, semanticModel)
            || killer.SpanStart <= victim.SpanStart
            || killer.SpanStart >= reference.Span.End
            || ExitsBetween(victim, killer))
        {
            return false;
        }

        for (var current = killer.Parent; current is not null && !current.Span.Contains(reference.Span); current = current.Parent)
        {
            if (current is not (BlockSyntax or ExpressionStatementSyntax or ArgumentListSyntax or InvocationExpressionSyntax))
            {
                return false;
            }
        }

        return true;
    }

    // A killer must fully DETERMINE the new value: a simple non-tuple assignment, or an
    // `out` handoff (the language requires the callee to assign before returning).
    private static bool IsDeterminingWrite(SyntaxNode killer, ISymbol variable, SemanticModel semanticModel)
    {
        return killer switch
        {
            AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Left is not TupleExpressionSyntax
                && WritesVariable(assignment.Left, variable, semanticModel),
            ArgumentSyntax argument => argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)
                && WritesVariable(argument.Expression, variable, semanticModel),
            _ => false,
        };
    }

    // A return/throw/goto/break/continue/yield starting between the two writes diverts
    // control past the killer with the victim's value still bound (an early return in an
    // out-helper hands that delegate to the caller). Only the killer's OWN function counts:
    // an exit inside a nested lambda -- e.g. a `return` in the victim's own delegate body --
    // diverts nothing here, and neither does a break/continue/case-goto whose OWN construct
    // ends before the killer (control leaves the switch/loop and still reaches it).
    private static bool ExitsBetween(SyntaxNode victim, SyntaxNode killer)
    {
        var function = killer.Ancestors().FirstOrDefault(IsFunctionBoundary) ?? killer.SyntaxTree.GetRoot();
        foreach (var node in function.DescendantNodes(descendIntoChildren: n => ReferenceEquals(n, function) || !IsFunctionBoundary(n)))
        {
            if (node is ReturnStatementSyntax or ThrowStatementSyntax or GotoStatementSyntax
                    or BreakStatementSyntax or ContinueStatementSyntax or YieldStatementSyntax
                && victim.SpanStart < node.SpanStart && node.SpanStart < killer.SpanStart
                && !(ExitTarget(node) is { } target && target.Span.End <= killer.SpanStart))
            {
                return true;
            }
        }

        return false;
    }

    // The construct a contained exit leaves: the nearest switch/loop for `break`, the
    // nearest loop for `continue`, the nearest switch for `goto case`/`goto default`.
    // Returns null for the truly divergent exits (return/throw/yield, labeled goto).
    private static SyntaxNode? ExitTarget(SyntaxNode exit)
    {
        return exit switch
        {
            BreakStatementSyntax => exit.Ancestors().FirstOrDefault(static ancestor =>
                ancestor is SwitchStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax),
            ContinueStatementSyntax => exit.Ancestors().FirstOrDefault(static ancestor =>
                ancestor is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax),
            GotoStatementSyntax @goto when @goto.IsKind(SyntaxKind.GotoCaseStatement) || @goto.IsKind(SyntaxKind.GotoDefaultStatement)
                => exit.Ancestors().FirstOrDefault(static ancestor => ancestor is SwitchStatementSyntax),
            _ => null,
        };
    }

    private static bool IsFunctionBoundary(SyntaxNode node)
    {
        return node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
            or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax;
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
    // it counts, UNLESS that function is a LOCAL FUNCTION none of whose references can run
    // before the read (dead code, or only called later). In the same function, one textually
    // before the reference obviously precedes it, and one textually after still reaches it
    // when a loop encloses both AND the variable outlives the iteration (a loop-body local is
    // freshly initialized each pass). Shared by MZR004's delegate-reassignment scan and the
    // provenance checks in ReceiverChains.
    public static bool CanExecuteBefore(SyntaxNode assignment, SyntaxNode reference, ISymbol variable, SemanticModel? semanticModel)
    {
        return CanExecuteBefore(assignment, reference, variable, semanticModel, visitedFunctions: null);
    }

    private static bool CanExecuteBefore(SyntaxNode node, SyntaxNode reference, ISymbol variable, SemanticModel? semanticModel, HashSet<SyntaxNode>? visitedFunctions)
    {
        var enclosing = EnclosingFunction(node);
        if (!ReferenceEquals(enclosing, EnclosingFunction(reference)))
        {
            return CouldRunBefore(enclosing, reference, variable, semanticModel, visitedFunctions);
        }

        if (node.SpanStart < reference.SpanStart)
        {
            return true;
        }

        for (var current = node.Parent; current is not null; current = current.Parent)
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
            // An arrow body belongs to the declaration that carries it: attributing it to
            // the OWNER lets an expression-bodied local function get the same
            // reference-ordering as a block-bodied one instead of counting as unknowable.
            if (current is ArrowExpressionClauseSyntax { Parent: { } owner })
            {
                return owner;
            }

            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
                or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)
            {
                return current;
            }
        }

        return null;
    }

    // A write inside a LOCAL FUNCTION runs only when some call site does: it can precede the
    // read only if a REFERENCE to the function can (each reference is ordered recursively --
    // the visited set breaks mutual-recursion cycles). A write inside a LAMBDA runs where
    // the built delegate does: immediately for an immediately-invoked one, at the receiving
    // variable's invocation sites for a lifted one -- a callback that is merely BUILT and
    // never runs before the read cannot feed it. Anything else -- a method or accessor,
    // reachable from outside this tree, or a lambda that escapes -- stays unknowable and
    // counts.
    private static bool CouldRunBefore(SyntaxNode? function, SyntaxNode reference, ISymbol variable, SemanticModel? semanticModel, HashSet<SyntaxNode>? visitedFunctions)
    {
        if (semanticModel is null)
        {
            return true;
        }

        if (function is AnonymousFunctionExpressionSyntax lambda)
        {
            visitedFunctions ??= new HashSet<SyntaxNode>();
            return visitedFunctions.Add(lambda) && LambdaRunsBefore(lambda, reference, variable, semanticModel, visitedFunctions);
        }

        if (function is not LocalFunctionStatementSyntax local
            || semanticModel.GetDeclaredSymbol(local) is not { } symbol)
        {
            return true;
        }

        visitedFunctions ??= new HashSet<SyntaxNode>();
        if (!visitedFunctions.Add(local))
        {
            return false;
        }

        foreach (var identifier in semanticModel.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText == symbol.Name
                && !local.Span.Contains(identifier.Span)
                && !IsInsideNameOfSyntax(identifier)
                && SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(identifier).Symbol, symbol)
                && ReferenceRunsBefore(identifier, reference, variable, semanticModel, visitedFunctions))
            {
                return true;
            }
        }

        return false;
    }

    // A name mentioned only inside nameof() is a compile-time string: it neither runs the
    // function nor lets a delegate escape, so the ordering scans must not count it.
    private static bool IsInsideNameOfSyntax(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
            {
                return true;
            }
        }

        return false;
    }

    // Where a lambda's body can run: at the invocation site for an immediately-invoked one
    // (`(() => ...)()`), at the receiving variable's invocation sites for one lifted into a
    // delegate variable -- resolved with the same machinery as method-group lifts. A lambda
    // that goes anywhere else (an argument, a return value) escapes to unknowable callers.
    private static bool LambdaRunsBefore(AnonymousFunctionExpressionSyntax lambda, SyntaxNode reference, ISymbol variable, SemanticModel semanticModel, HashSet<SyntaxNode> visitedFunctions)
    {
        SyntaxNode current = lambda;
        while (current.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            current = current.Parent;
        }

        if (current.Parent is InvocationExpressionSyntax { Expression: { } invoked } invocation && invoked == current)
        {
            return CanExecuteBefore(invocation, reference, variable, semanticModel, visitedFunctions);
        }

        var lifted = LiftTargetVariable(current, semanticModel);
        return lifted is null || LiftedDelegateRunsBefore(lifted, reference, variable, semanticModel, visitedFunctions);
    }

    // A direct CALL runs the function at the call's own position. A method-group LIFT runs it
    // wherever the receiving delegate variable is invoked -- so those invocation sites become
    // the ordering points; a lift that escapes anywhere else stays unknowable. Casts and
    // parentheses change neither: `(Action)Rebind` lifted into a variable is still ordered by
    // that variable's invocation sites.
    private static bool ReferenceRunsBefore(IdentifierNameSyntax use, SyntaxNode reference, ISymbol variable, SemanticModel semanticModel, HashSet<SyntaxNode> visitedFunctions)
    {
        var current = UnwrapCastsAndParentheses(use);
        if (current.Parent is InvocationExpressionSyntax { Expression: { } invoked } && invoked == current)
        {
            return CanExecuteBefore(current, reference, variable, semanticModel, visitedFunctions);
        }

        var lifted = LiftTargetVariable(current, semanticModel);
        if (lifted is null)
        {
            return true;
        }

        return LiftedDelegateRunsBefore(lifted, reference, variable, semanticModel, visitedFunctions);
    }

    private static SyntaxNode UnwrapCastsAndParentheses(SyntaxNode node)
    {
        while (node.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            node = node.Parent;
        }

        return node;
    }

    private static ISymbol? LiftTargetVariable(SyntaxNode use, SemanticModel semanticModel)
    {
        return use.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } => semanticModel.GetDeclaredSymbol(declarator),
            AssignmentExpressionSyntax assignment when assignment.Right == use && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                => semanticModel.GetSymbolInfo(assignment.Left).Symbol,
            _ => null,
        };
    }

    private static bool LiftedDelegateRunsBefore(ISymbol lifted, SyntaxNode reference, ISymbol variable, SemanticModel semanticModel, HashSet<SyntaxNode> visitedFunctions)
    {
        foreach (var name in semanticModel.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (name.Identifier.ValueText != lifted.Name
                || IsInsideNameOfSyntax(name)
                || !SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(name).Symbol, lifted))
            {
                continue;
            }

            if (LiftedUseRunsBefore(name, reference, variable, semanticModel, visitedFunctions))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LiftedUseRunsBefore(IdentifierNameSyntax name, SyntaxNode reference, ISymbol variable, SemanticModel semanticModel, HashSet<SyntaxNode> visitedFunctions)
    {
        var current = UnwrapCastsAndParentheses(name);
        if (current.Parent is InvocationExpressionSyntax { Expression: { } invoked } && invoked == current)
        {
            return CanExecuteBefore(current, reference, variable, semanticModel, visitedFunctions);
        }

        // A write to the variable is not a use of the delegate, and a DISCARD
        // (`_ = unused;`) provably drops it; anything else lets the delegate escape to
        // unknowable invocation sites -- but only an escape that can itself RUN before
        // the read can put an invocation before it.
        if (current.Parent is AssignmentExpressionSyntax write
            && (write.Left == current || semanticModel.GetSymbolInfo(write.Left).Symbol is IDiscardSymbol))
        {
            return false;
        }

        return CanExecuteBefore(current, reference, variable, semanticModel, visitedFunctions);
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

    // Same-tree code an operation EXECUTES, whatever the syntax: a call; a property access (a
    // read runs the getter, an assignment target runs the setter, compound forms run both); a
    // constructor; or a user-defined operator/conversion. Shared by MZR003's and MZR004's
    // executed chases. (A member mentioned only in nameof is not executed -- callers guard.)
    public static IEnumerable<IMethodSymbol> ExecutedMethods(IOperation operation)
    {
        switch (operation)
        {
            case IInvocationOperation invocation:
                yield return invocation.TargetMethod;
                break;
            case IPropertyReferenceOperation property:
                var (reads, writes) = PropertyUsage(property);
                if (reads && property.Property.GetMethod is { } getter)
                {
                    yield return getter;
                }

                if (writes && property.Property.SetMethod is { } setter)
                {
                    yield return setter;
                }

                break;
            case IObjectCreationOperation { Constructor: { } constructor }:
                yield return constructor;
                break;
            case IBinaryOperation { OperatorMethod: { } binaryOperator }:
                yield return binaryOperator;
                break;
            case IUnaryOperation { OperatorMethod: { } unaryOperator }:
                yield return unaryOperator;
                break;
            case IConversionOperation { OperatorMethod: { } conversionOperator }:
                yield return conversionOperator;
                break;

            // `Changed += handler` executes the custom add (or remove) accessor immediately.
            case IEventAssignmentOperation { EventReference: IEventReferenceOperation eventReference } eventAssignment:
                var accessor = eventAssignment.Adds ? eventReference.Event.AddMethod : eventReference.Event.RemoveMethod;
                if (accessor is not null)
                {
                    yield return accessor;
                }

                break;

            // `using` runs Dispose/DisposeAsync before the evaluation exits: the resource
            // type's concrete implementation is what actually runs.
            case IUsingDeclarationOperation usingDeclaration:
                foreach (var dispose in DisposeMethods(ResourceTypes(usingDeclaration.DeclarationGroup), usingDeclaration.IsAsynchronous))
                {
                    yield return dispose;
                }

                break;
            case IUsingOperation usingOperation:
                foreach (var dispose in DisposeMethods(ResourceTypes(usingOperation.Resources), usingOperation.IsAsynchronous))
                {
                    yield return dispose;
                }

                break;
        }
    }

    private static IEnumerable<IMethodSymbol> DisposeMethods(IEnumerable<ITypeSymbol?> resourceTypes, bool isAsynchronous)
    {
        foreach (var type in resourceTypes)
        {
            if (ConcreteDispose(type, isAsynchronous) is { } dispose)
            {
                yield return dispose;
            }
        }
    }

    private static IMethodSymbol? ConcreteDispose(ITypeSymbol? type, bool isAsynchronous)
    {
        if (type is null)
        {
            return null;
        }

        var interfaceName = isAsynchronous ? "IAsyncDisposable" : "IDisposable";
        var methodName = isAsynchronous ? "DisposeAsync" : "Dispose";
        foreach (var implemented in type.AllInterfaces)
        {
            if (implemented.Name == interfaceName && implemented.ContainingNamespace?.ToDisplayString() == "System"
                && implemented.GetMembers(methodName).FirstOrDefault() is IMethodSymbol member
                && type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
            {
                return implementation;
            }
        }

        // Pattern-based disposal (ref structs): a parameterless instance Dispose.
        return type.GetMembers(methodName).OfType<IMethodSymbol>()
            .FirstOrDefault(candidate => !candidate.IsStatic && candidate.Parameters.Length == 0);
    }

    // The INITIALIZER's type beats the declared type: `using IDisposable _ = new Writer(v);`
    // runs Writer.Dispose, not an unwalkable interface member.
    private static IEnumerable<ITypeSymbol?> ResourceTypes(IOperation? resources)
    {
        switch (resources)
        {
            case IVariableDeclarationGroupOperation group:
                foreach (var declaration in group.Declarations)
                {
                    foreach (var declarator in declaration.Declarators)
                    {
                        yield return UnwrappedType(declarator.Initializer?.Value) ?? declarator.Symbol.Type;
                    }
                }

                break;
            case { } expression:
                yield return UnwrappedType(expression) ?? expression.Type;
                break;
        }
    }

    private static ITypeSymbol? UnwrappedType(IOperation? value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value?.Type;
    }

    // How an access uses the property: a plain read runs the getter, an assignment target
    // the setter, compound forms both. Shared with MZR002's getter chase.
    public static (bool Reads, bool Writes) PropertyUsage(IPropertyReferenceOperation property)
    {
        return property.Parent switch
        {
            ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Target, property) => (false, true),
            ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, property) => (true, true),
            ICoalesceAssignmentOperation coalesce when ReferenceEquals(coalesce.Target, property) => (true, true),
            IIncrementOrDecrementOperation increment when ReferenceEquals(increment.Target, property) => (true, true),
            _ => (true, false),
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
