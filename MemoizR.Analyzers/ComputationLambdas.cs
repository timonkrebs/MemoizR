using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// Shared plumbing for the rules that inspect computation bodies (MZR002, MZR003, MZR004):
// finding the computations among a factory invocation's arguments -- anonymous functions, and
// method groups / local functions declared in the same file -- resolving the delegates they
// store and invoke, walking their bodies, and locating where to report. Everything here is
// RESOLUTION: MZR004 layers its unverifiable-means-flagged accounting on top.
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
                foreach (var body in BodiesFromVariableInitializer(ReferencedVariable(value), value, semanticModel, visitedVariables))
                {
                    yield return body;
                }

                break;

            // A conditional (null-coalescing, switch-expression) computation stores whichever
            // arm the flow picks: every arm is a possible body, so every arm is walked.
            case IConditionalOperation or ICoalesceOperation or ISwitchExpressionOperation:
                foreach (var body in ConditionalArms(value)!.SelectMany(arm => BodiesIn(arm, semanticModel, visitedVariables)))
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

    // A null-conditional invoke's receiver surfaces as a conditional-access PLACEHOLDER
    // (`d?.Invoke()`): the real delegate expression is the enclosing conditional access's
    // operation. Shared by every invoked-delegate chase.
    public static IOperation ResolveConditionalReceiver(IOperation callee)
    {
        if (callee is IConditionalAccessInstanceOperation placeholder)
        {
            for (var current = placeholder.Parent; current is not null; current = current.Parent)
            {
                if (current is IConditionalAccessOperation conditional)
                {
                    return conditional.Operation;
                }
            }
        }

        return callee;
    }

    // An operation stripped of its implicit/explicit conversion wrappers -- the shape every
    // resolution here actually wants to match on.
    public static IOperation Unwrap(IOperation value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value;
    }

    // The declared STORAGE a reference reads: a local, a field, or a property.
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

    // ReferencedVariable widened to PARAMETERS, for the chases that hop through a callee's
    // binding as readily as through storage (a parameter has no declaration initializer:
    // only its initializing write, if any, resolves).
    public static ISymbol? ReferencedSymbol(IOperation reference)
    {
        return reference is IParameterReferenceOperation parameter ? parameter.Parameter : ReferencedVariable(reference);
    }

    // The direct ARMS of a conditional, coalesced, or switch-expression value: each is a
    // candidate the flow can pick, and each resolves independently (ValueArms flattens
    // nesting). Null when the value is none of those -- callers distinguish "not a
    // conditional" from "one arm".
    public static IReadOnlyList<IOperation>? ConditionalArms(IOperation value)
    {
        return value switch
        {
            IConditionalOperation { WhenFalse: { } whenFalse } conditional => new[] { conditional.WhenTrue, whenFalse },
            IConditionalOperation conditional => new[] { conditional.WhenTrue },
            ICoalesceOperation coalesce => new[] { coalesce.Value, coalesce.WhenNull },
            ISwitchExpressionOperation switchExpression => switchExpression.Arms.Select(arm => arm.Value).ToArray(),
            _ => null,
        };
    }

    // Every value a body RETURNS, on its own execution path (built callbacks' returns
    // belong to the callback, not to this body -- hence the pruned walk).
    public static IEnumerable<IOperation> ReturnedValues(IOperation body)
    {
        return DescendDirectExecution(body)
            .OfType<IReturnOperation>()
            .Select(returnOperation => returnOperation.ReturnedValue)
            .OfType<IOperation>();
    }

    // `Func<Task<int>> compute = async () => ...; f.CreateMemoizR(compute);` is the same
    // computation as the inline form, reached through a variable. Best-effort resolution: the
    // variable's same-tree INITIALIZER, unless a reassignment definitely replaced it (then the
    // surviving-write resolution owns the value; a reassignment that MAY run keeps the
    // best-effort trust, and the runtime checks cover what this cannot see). The visited set
    // breaks initializer reference cycles (two fields initialized from each other).
    private static IEnumerable<ComputationBody> BodiesFromVariableInitializer(ISymbol? variable, IOperation reference, SemanticModel? semanticModel, HashSet<ISymbol>? visitedVariables)
    {
        visitedVariables ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (variable is null || !visitedVariables.Add(variable))
        {
            yield break;
        }

        // A definitely-overwritten initializer can never be the stored value at THIS read:
        // walking it would charge (or excuse) a closure the overwrite provably replaced --
        // the surviving-write resolution owns that case. May-be-overwritten initializers
        // keep the best-effort trust below.
        var operation = SameTreeInitializerOperation(variable, semanticModel);
        if (operation is null
            || (semanticModel is not null && InitializerDefinitelyOverwritten(variable, reference, semanticModel)))
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
    // initializer here: the callers that chase them resolve the getter's returns instead.
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
        // (An out-argument handoff has no right-hand side here: its bodies come from the
        // callee's assignments, which the out-handoff resolution walks.)
        if (initializer is null && EffectiveInitializerWrite(variable, semanticModel) is AssignmentExpressionSyntax assignment)
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

    // The single same-tree WRITE standing in for a missing declaration initializer -- a
    // plain `x = value` (or simple deconstruction), or an OUT-argument handoff
    // (`Provide(out patch)`, which the language requires to assign) -- or null when the
    // declaration has an initializer, there are several writes (ambiguous), or the one
    // write does not fully determine the value. Reassignment scans EXCLUDE this node: it is
    // the initialization, not a rebind.
    public static SyntaxNode? EffectiveInitializerWrite(ISymbol variable, SemanticModel? semanticModel, SyntaxNode? readSite = null)
    {
        if (semanticModel is null
            || (variable.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is { } declaration && DeclaredInitializerSite(declaration) is not null))
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
            var target = ReassignmentTargets(node)?.FirstOrDefault(candidate => WritesVariable(candidate, variable, semanticModel));
            if (target is null || (readSite is not null && !CanExecuteBefore(node, readSite, variable, semanticModel)))
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

        if (readSite is null || !ReadsThroughOwnReceiver(readSite, variable))
        {
            return false;
        }

        var function = EnclosingFunction(write);
        return function is not null
            && (ReferenceEquals(function, EnclosingFunction(readSite)) || function.Span.Contains(readSite.Span));
    }

    // The synthesis pairs a write through the member's OWN object with a read through it
    // too: `other.Patch` reads some other instance, which the `this`-receiver write says
    // nothing about -- that instance's copy may still be null or externally supplied.
    // Statics have no receiver to mismatch.
    private static bool ReadsThroughOwnReceiver(SyntaxNode readSite, ISymbol variable)
    {
        return variable.IsStatic
            || readSite is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
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
            if (IsFunctionBoundary(current) || current is ArrowExpressionClauseSyntax or CompilationUnitSyntax)
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
    public static bool IsWrittenBefore(ISymbol variable, SyntaxNode reference, SemanticModel? semanticModel, SyntaxNode? excluding = null)
    {
        if (semanticModel is null)
        {
            return true;
        }

        return semanticModel.SyntaxTree.GetRoot().DescendantNodes().Any(node =>
            !ReferenceEquals(node, excluding)
            && ReassignmentTargets(node) is { } targets
            && targets.Any(target => WritesVariable(target, variable, semanticModel))
            && CanExecuteBefore(node, reference, variable, semanticModel));
    }

    // Call-site arguments substitute for a chased helper's parameters: maps are built
    // pre-substituted, so nested helper calls resolve through to the original computation's
    // operations. A property SETTER's implicit `value` parameter maps to the assignment's
    // right-hand side, and an INDEXER's index arguments map like call arguments (aliased
    // onto the accessors' own parameter symbols, which is what accessor bodies bind). The
    // OUTER bindings carry through -- a called local function closes over its enclosing
    // helper's parameters (`void Inner() => s.Set(2); Inner();`), so dropping them would
    // orphan those references; keys are parameter symbols, so unrelated callees cannot
    // collide, and a recursive call's fresh binding overwrites the stale one.
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
                ? entry.Key.Name
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

    // The value bound to a parameter, tolerating GENERIC construction: a map built from
    // `Make<List<int>>` is keyed by that method's constructed parameters, while the walked
    // body references `Make<T>`'s originals, and the two are not symbol-equal.
    public static IOperation? MappedValue(Dictionary<IParameterSymbol, IOperation>? argumentMap, IParameterSymbol parameter)
    {
        if (argumentMap is null)
        {
            return null;
        }

        if (argumentMap.TryGetValue(parameter, out var value))
        {
            return value;
        }

        return argumentMap
            .Where(entry => SymbolEqualityComparer.Default.Equals(entry.Key.OriginalDefinition, parameter.OriginalDefinition))
            .Select(entry => entry.Value)
            .FirstOrDefault();
    }

    public static IOperation? SubstituteArguments(IOperation? reference, Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        if (reference is null)
        {
            return null;
        }

        var current = Unwrap(reference);

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
    // too.
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

    // The delegate-typed arguments of an Apply call. Only the PATCH parameter stores a
    // delegate: the state argument (possibly a computed property) must not run the
    // delegate-shaped resolutions.
    public static IEnumerable<IOperation> PatchArguments(IInvocationOperation invocation)
    {
        return invocation.Arguments
            .Where(argument => argument.Parameter?.Type is { TypeKind: TypeKind.Delegate })
            .Select(argument => argument.Value);
    }

    // Every supplemental patch body an Apply call stores (nothing for any other host):
    // MZR002 and MZR003 walk these exactly like inline patches, because they replay on the
    // state's flows all the same.
    public static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> AssembledPatchBodies(IInvocationOperation invocation)
    {
        return FactoryMethods.IsOptimisticPatchHost(invocation.TargetMethod)
            ? PatchArguments(invocation).SelectMany(value => AssembledPatchBodies(value, invocation.SemanticModel))
            : Enumerable.Empty<(ComputationBody, Dictionary<IParameterSymbol, IOperation>?)>();
    }

    // Best-effort SUPPLEMENTAL bodies for an Apply patch argument beyond OfArgumentValue: a
    // patch assembled by a same-tree out-helper (the sole dominating out handoff, following
    // forwarded handoffs), the surviving writes over a definitely overwritten initializer,
    // or the returns of a same-tree computed get-only delegate property or delegate factory.
    // Resolution only -- MZR004 owns the unverifiable accounting for these shapes; MZR003
    // needs the BODIES, because a Set inside them still throws under the evaluation lock.
    public static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> AssembledPatchBodies(IOperation value, SemanticModel? semanticModel)
    {
        var reference = Unwrap(value);

        // A conditional patch stores whichever arm the flow picked: each arm resolves
        // through this same supplemental chain separately, so a direct lambda arm cannot
        // hide an assembled sibling.
        if (ConditionalArms(reference) is not null)
        {
            foreach (var body in ValueArms(reference).SelectMany(arm => AssembledPatchBodies(arm, semanticModel)))
            {
                yield return body;
            }

            yield break;
        }

        var variable = ReferencedSymbol(reference);
        if (variable is not null && EffectiveInitializerWrite(variable, semanticModel, reference.Syntax) is ArgumentSyntax outWrite)
        {
            foreach (var body in OutHandoffBodies(outWrite, semanticModel, new HashSet<SyntaxNode>(), outerMap: null))
            {
                yield return body;
            }
        }

        // A declaration initializer definitely OVERWRITTEN before its read leaves the
        // surviving writes as the only closures the call can store -- at EVERY alias link:
        // `src = safe; src = other; var patch = src;` stores src's surviving write even
        // though the patch variable itself is never rebound. (Resolution-only, like
        // everything here -- MZR004 owns the accounting.)
        foreach (var body in AliasChainSurvivingBodies(reference, semanticModel))
        {
            yield return body;
        }

        // An ALIAS resolves to its assembled source first: `Func<int,int> p = Patch;`
        // stores whatever the computed property's getter (or the stored factory call)
        // returned.
        foreach (var body in AssembledSourceBodies(ResolveDelegateValue(reference, semanticModel, null), semanticModel))
        {
            yield return body;
        }
    }

    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> AssembledSourceBodies(IOperation source, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? outerMap = null)
    {
        source = Unwrap(source);
        var resolvedVariable = ReferencedSymbol(source);

        // The INDEXER's argument map keeps the returned lambda's index-parameter references
        // resolvable (`this[Signal<int> s] { get { return x => { s.Set(1); ... }; } }`).
        if (resolvedVariable is IPropertySymbol { GetMethod: { } getter, SetMethod: null }
            && ResolveMethodBody(getter, semanticModel) is { } getterBody)
        {
            return ReturnedBodies(getterBody, semanticModel, BuildArgumentMap(source, outerMap));
        }

        var stored = Unwrap(SameTreeInitializerOperation(resolvedVariable, semanticModel));

        // A CONDITIONAL initializer stores whichever arm ran, so each arm is an assembled
        // source of its own (`Func<int,int> patch = flag ? safe : Make(v);`).
        if (stored is not null && ConditionalArms(stored) is not null)
        {
            return ValueArms(stored).SelectMany(arm => AssembledSourceBodies(arm, semanticModel, outerMap));
        }

        return AssembledCallBodies(source, stored, semanticModel, outerMap);
    }

    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> AssembledCallBodies(
        IOperation source,
        IOperation? stored,
        SemanticModel? semanticModel,
        Dictionary<IParameterSymbol, IOperation>? outerMap)
    {
        // A patch produced by a same-tree delegate FACTORY -- called inline, or stored
        // through the variable's initializer (a parameter's initializing write included) --
        // resolves through the factory's returns, with the call's own argument map.
        var call = source as IInvocationOperation ?? stored as IInvocationOperation;
        if (call is null || ResolveMethodBody(call.TargetMethod, semanticModel) is not { } factoryBody)
        {
            yield break;
        }

        foreach (var entry in ReturnedBodies(factoryBody, semanticModel, BuildArgumentMap(call, outerMap)))
        {
            yield return entry;
        }
    }

    // The surviving-write carve-out at every ALIAS LINK's read site, mirroring
    // ResolveDelegateValue's hops: a link that was written owns its value (the chase below
    // covers its definite-overwrite case), so the chain stops there.
    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> AliasChainSurvivingBodies(IOperation reference, SemanticModel? semanticModel)
    {
        var link = reference;
        HashSet<ISymbol>? visited = null;
        while (ReferencedVariable(link) is { } variable)
        {
            visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (!visited.Add(variable))
            {
                yield break;
            }

            foreach (var body in SurvivingWriteBodies(variable, link, semanticModel))
            {
                yield return body;
            }

            if (SameTreeInitializerOperation(variable, semanticModel) is not { } next
                || !IsReferenceShape(next)
                || IsWrittenBefore(variable, link.Syntax, semanticModel))
            {
                yield break;
            }

            link = Unwrap(next);
        }
    }

    // The reassignment carve-out, resolution-only: when the declaration initializer is
    // definitely overwritten on the straight-line path to this read, only the surviving
    // writes can be the stored closure -- each write that no other candidate definitely
    // kills resolves like an out-helper's assignment (values through aliases, forwarded
    // handoffs recursively). MZR002/MZR003 walk these; MZR004's carve-out accounts.
    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> SurvivingWriteBodies(
        ISymbol variable,
        IOperation reference,
        SemanticModel? semanticModel)
    {
        if (semanticModel is null || StaleDeclarator(variable, reference, semanticModel) is not { } declarator)
        {
            yield break;
        }

        var writes = WritesBefore(variable, reference.Syntax, semanticModel);
        if (!writes.Any(write => DefinitelyOverwrites(write, declarator, reference.Syntax, variable, semanticModel)))
        {
            yield break;
        }

        foreach (var write in Surviving(writes, reference.Syntax, variable, semanticModel))
        {
            foreach (var body in OutHandoffNodeBodies(write, variable, semanticModel, new HashSet<SyntaxNode>(), callMap: null))
            {
                yield return body;
            }
        }
    }

    private static bool InitializerDefinitelyOverwritten(ISymbol variable, IOperation reference, SemanticModel semanticModel)
    {
        return StaleDeclarator(variable, reference, semanticModel) is { } declarator
            && WritesBefore(variable, reference.Syntax, semanticModel)
                .Any(node => DefinitelyOverwrites(node, declarator, reference.Syntax, variable, semanticModel));
    }

    // The same-tree declaration initializer a write CAN go stale at, for this read. A
    // member read through some OTHER receiver observes that instance's storage: the
    // own-receiver writes the scans count say nothing definite about it.
    private static SyntaxNode? StaleDeclarator(ISymbol variable, IOperation reference, SemanticModel semanticModel)
    {
        return ReadsThroughOwnReceiver(reference.Syntax, variable)
            && variable.DeclaringSyntaxReferences.FirstOrDefault() is { } declaration
            && declaration.SyntaxTree == semanticModel.SyntaxTree
            ? DeclaredInitializerSite(declaration.GetSyntax())
            : null;
    }

    // Every reachable same-tree write to the variable that can run before the read.
    public static List<SyntaxNode> WritesBefore(ISymbol variable, SyntaxNode reference, SemanticModel semanticModel)
    {
        return semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .Where(node => IsVariableWriteNode(node, variable, semanticModel)
                && CanExecuteBefore(node, reference, variable, semanticModel))
            .ToList();
    }

    // Every declaration shape SameTreeInitializerOperation trusts can go stale the same way:
    // a local/field declarator, an auto-property initializer, a deconstruction designation.
    public static SyntaxNode? DeclaredInitializerSite(SyntaxNode declaration)
    {
        return declaration switch
        {
            VariableDeclaratorSyntax { Initializer: not null } => declaration,
            PropertyDeclarationSyntax { Initializer: not null } => declaration,
            SingleVariableDesignationSyntax => declaration,
            _ => null,
        };
    }

    // A delegate VALUE collapsed through variable-alias initializers and parameter-to-
    // argument hops. Hops stop at anything the body resolution can consume directly (a
    // lambda, a method group, a variable holding one) so no shape is lost, and at any
    // alias or parameter WRITTEN before its read: the write (not the initializer or the
    // call site) owns the value, and hopping past it would resurrect a stale one. The
    // aliasRebound policy decides what counts as written for an ALIAS link -- MZR004 excuses
    // the write standing in for a missing initializer; resolution-only callers excuse none.
    public static IOperation ResolveDelegateValue(
        IOperation value,
        SemanticModel? semanticModel,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        Func<ISymbol, IOperation, bool>? aliasRebound = null)
    {
        var current = value;
        HashSet<ISymbol>? visited = null;
        while (true)
        {
            var unwrapped = Unwrap(current);

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
            if (!visited.Add(variable)
                || SameTreeInitializerOperation(variable, semanticModel) is not { } initializer
                || !IsReferenceShape(initializer)
                || AliasRebound(variable, current, semanticModel, aliasRebound))
            {
                return current;
            }

            current = initializer;
        }
    }

    // The bodies a synchronously INVOKED delegate runs, each with the argument map it
    // resolved under, the invoke's own arguments bound to the body's parameters. A
    // conditional callee is resolved per ARM; within an arm, direct resolutions (a same-tree
    // lambda or method group, through aliases and parameter bindings, null-conditional
    // invokes unwrapped; a conditionally rebound parameter's handed-in value beside its
    // rebind) come first, and only when none resolve does the callee's own SOURCE decide --
    // a factory call's returns, a computed get-only property's, or the initializer's
    // factory. Resolution only: the callers own what they do per body, and MZR004 keeps its
    // accounting-rich chase.
    public static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> InvokedDelegateBodies(
        IInvocationOperation invoke,
        SemanticModel? semanticModel,
        Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        if (invoke.Instance is not { } callee)
        {
            yield break;
        }

        var resolved = ResolveDelegateValue(ResolveConditionalReceiver(callee), semanticModel, argumentMap);

        // A conditional callee runs whichever arm the flow picked: each is resolved on its
        // own, so a directly-resolvable arm cannot stop the assembled chase for a sibling.
        foreach (var arm in ValueArms(resolved))
        {
            foreach (var entry in InvokedArmBodies(arm, invoke, semanticModel, argumentMap))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> InvokedArmBodies(
        IOperation arm,
        IInvocationOperation invoke,
        SemanticModel? semanticModel,
        Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        var resolved = ResolveDelegateValue(arm, semanticModel, argumentMap);
        var found = false;
        foreach (var body in OfArgumentValue(resolved, semanticModel))
        {
            found = true;
            yield return (body, BindInvokedParameters(body, invoke, argumentMap, semanticModel));
        }

        // A REBOUND parameter stops the hop, but what the caller handed in still runs when
        // the rebind is conditional: both stay candidates.
        if (Unwrap(resolved) is IParameterReferenceOperation rebound
            && argumentMap?.TryGetValue(rebound.Parameter, out var handed) == true)
        {
            foreach (var body in OfArgumentValue(handed, semanticModel))
            {
                found = true;
                yield return (body, BindInvokedParameters(body, invoke, argumentMap, semanticModel));
            }
        }

        if (found)
        {
            yield break;
        }

        // `Get()(x)` runs whatever the factory returned, `Step()` whatever the getter did,
        // and `Func<int,int> d = Make(v); d(x)` whatever the INITIALIZER's factory did --
        // the same three sources an assembled patch argument resolves through.
        foreach (var (body, map) in AssembledSourceBodies(resolved, semanticModel, argumentMap))
        {
            yield return (body, BindInvokedParameters(body, invoke, map, semanticModel));
        }
    }

    // The RECEIVER a method-group callee captured (`Action step = other.Touch;`), or null
    // when the callee is a lambda or a static target. MZR002 needs it: that body's `this`
    // is the captured object, not the computation's. Follows the same hops that FIND the
    // body: the alias collapse stops at a variable whose initializer is a method GROUP (not
    // a reference shape), so the method-group resolution walks the initializer chain on.
    public static IOperation? InvokedDelegateReceiver(IInvocationOperation invoke, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        return invoke.Instance is { } callee
            ? ResolveMethodReference(ResolveDelegateValue(ResolveConditionalReceiver(callee), semanticModel, argumentMap), semanticModel)?.Instance
            : null;
    }

    // The method group a delegate value holds: the group itself, a conversion or
    // delegate-creation wrapper over it, or a variable whose (same-tree) initializer holds
    // it -- so a `Func<int,int> patch = helper.Patch;` stored one statement earlier does not
    // hide the receiver. GetOperation on an initializer yields the reference without the
    // delegate-creation wrapper, hence the bare case. The visited set breaks initializer
    // cycles.
    public static IMethodReferenceOperation? ResolveMethodReference(IOperation? value, SemanticModel? semanticModel)
    {
        HashSet<ISymbol>? visitedVariables = null;
        while (true)
        {
            switch (value)
            {
                case IConversionOperation conversion:
                    value = conversion.Operand;
                    continue;
                case IDelegateCreationOperation creation:
                    value = creation.Target;
                    continue;
                case IMethodReferenceOperation methodReference:
                    return methodReference;
                case ILocalReferenceOperation or IFieldReferenceOperation or IPropertyReferenceOperation:
                    visitedVariables ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    var variable = ReferencedVariable(value)!;
                    if (!visitedVariables.Add(variable))
                    {
                        return null;
                    }

                    value = SameTreeInitializerOperation(variable, semanticModel);
                    continue;
                default:
                    return null;
            }
        }
    }

    // The invoked delegate's OWN parameters bind to the call's arguments, exactly like a
    // called helper's: `Action<C> step = c => c.Counter++; step(this);` writes the
    // computation's instance, and `step(otherSignal)` carries that signal's provenance.
    private static Dictionary<IParameterSymbol, IOperation>? BindInvokedParameters(
        ComputationBody body,
        IInvocationOperation invoke,
        Dictionary<IParameterSymbol, IOperation>? outer,
        SemanticModel? semanticModel)
    {
        var parameters = BodyParameters(body, semanticModel);
        if (parameters.IsDefaultOrEmpty || invoke.Arguments.Length == 0)
        {
            return outer;
        }

        // The ARGUMENT's own parameter decides its slot, so `step(other: a, mine: b)` binds
        // by name; the delegate's Invoke parameters line up positionally with the resolved
        // body's, which is what carries the value to the lambda's own parameter symbol.
        var map = CopyOf(outer);
        foreach (var argument in invoke.Arguments)
        {
            if (argument.Parameter is { Ordinal: var ordinal } && ordinal < parameters.Length)
            {
                map[parameters[ordinal]] = argument.Value;
            }
        }

        return map;
    }

    // The parameters of a resolved body, whether it came from a lambda (its scope is the
    // anonymous function) or a method group / local function (a declaration).
    private static ImmutableArray<IParameterSymbol> BodyParameters(ComputationBody body, SemanticModel? semanticModel)
    {
        if (semanticModel is null || body.Scope.SyntaxTree != semanticModel.SyntaxTree)
        {
            return ImmutableArray<IParameterSymbol>.Empty;
        }

        var symbol = semanticModel.GetSymbolInfo(body.Scope).Symbol as IMethodSymbol
            ?? semanticModel.GetDeclaredSymbol(body.Scope) as IMethodSymbol;

        return symbol?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty;
    }

    // How far an alias chain may be followed is the ONE thing the two callers disagree on:
    // resolution-only chases stop at any same-tree write, while MZR004 excuses the write
    // that stands in for a missing initializer (its own IsReassignedBefore).
    private static bool AliasRebound(ISymbol variable, IOperation at, SemanticModel? semanticModel, Func<ISymbol, IOperation, bool>? custom)
    {
        return custom is null
            ? IsWrittenBefore(variable, at.Syntax, semanticModel)
            : custom(variable, at);
    }

    public static bool IsReferenceShape(IOperation operation)
    {
        return ReferencedSymbol(Unwrap(operation)) is not null;
    }

    // Each body carries the ARGUMENT MAP that resolved it: a nested factory's returns bind
    // that factory's own parameters, which the caller's map knows nothing about.
    public static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> ReturnedBodies(ComputationBody methodBody, SemanticModel? semanticModel, Dictionary<IParameterSymbol, IOperation>? argumentMap = null, HashSet<(IMethodSymbol, string)>? visitedFactories = null)
    {
        visitedFactories ??= new HashSet<(IMethodSymbol, string)>();

        // A conditional return stores whichever arm the flow picked: each arm is its own
        // candidate, so a direct lambda arm cannot silence a factory-call one.
        foreach (var arm in ReturnedValues(methodBody.Body).SelectMany(ValueArms))
        {
            foreach (var entry in ReturnedArmBodies(arm, semanticModel, argumentMap, visitedFactories))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> ReturnedArmBodies(
        IOperation arm,
        SemanticModel? semanticModel,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        HashSet<(IMethodSymbol, string)> visitedFactories)
    {
        // Aliases resolve too: `var q = p; return q;` hands back the call-site value.
        var resolved = ResolveDelegateValue(arm, semanticModel, argumentMap);
        var resolvedAny = false;
        foreach (var body in OfArgumentValue(resolved, semanticModel))
        {
            resolvedAny = true;
            yield return (body, argumentMap);
        }

        if (resolvedAny)
        {
            yield break;
        }

        foreach (var entry in NestedFactoryReturnedBodies(resolved, semanticModel, argumentMap, visitedFactories))
        {
            yield return entry;
        }
    }

    // The leaf ARMS of a conditional or coalesced value, conversions unwrapped: each is a
    // candidate the flow can pick, and each resolves independently.
    private static IEnumerable<IOperation> ValueArms(IOperation value)
    {
        var unwrapped = Unwrap(value);

        return ConditionalArms(unwrapped) is { } arms ? arms.SelectMany(ValueArms) : new[] { unwrapped };
    }

    // `return Make();` assembles one factory deeper: chased through the nested call's
    // returns with its own map, the visited set bounding recursive factories.
    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> NestedFactoryReturnedBodies(
        IOperation resolved,
        SemanticModel? semanticModel,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        HashSet<(IMethodSymbol, string)> visitedFactories)
    {
        resolved = Unwrap(resolved);

        if (resolved is not IInvocationOperation call
            || ResolveMethodBody(call.TargetMethod, semanticModel) is not { } nested)
        {
            yield break;
        }

        var nestedMap = BuildArgumentMap(call, argumentMap);
        if (!visitedFactories.Add((call.TargetMethod, ArgumentMapKey(nestedMap))))
        {
            yield break;
        }

        foreach (var entry in ReturnedBodies(nested, semanticModel, nestedMap, visitedFactories))
        {
            yield return entry;
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
            || ResolveOutHelper(argument, semanticModel, outerMap) is not var (parameter, _, helper, callMap))
        {
            yield break;
        }

        // The kill filter mirrors MZR004's accounting chase: a write -- assignment or
        // forwarded handoff -- definitely overwritten on the straight-line path to the
        // helper's return can never be the delegate the caller receives.
        var writes = helper.Scope.DescendantNodes().Where(node => IsVariableWriteNode(node, parameter, semanticModel)).ToList();
        foreach (var node in Surviving(writes, helper.Scope, parameter, semanticModel))
        {
            foreach (var body in OutHandoffNodeBodies(node, parameter, semanticModel, visited, callMap))
            {
                yield return body;
            }
        }
    }

    // The same-tree helper an out argument hands its variable to -- by the SEMANTIC
    // parameter, since named arguments reorder freely (`Provide(second: out later, first:
    // 0)`) -- with the call's own argument map: the helper's OTHER parameters resolve
    // through this call's arguments (`Provide(out later, static () => 0)` with
    // `Provide(out d, source) => d = source` hands the call-site lambda back through `d`).
    public static (IParameterSymbol Parameter, IMethodSymbol Method, ComputationBody Helper, Dictionary<IParameterSymbol, IOperation>? CallMap)? ResolveOutHelper(
        ArgumentSyntax argument,
        SemanticModel semanticModel,
        Dictionary<IParameterSymbol, IOperation>? outerMap)
    {
        if ((semanticModel.GetOperation(argument) as IArgumentOperation) is not { Parameter: { } parameter } argumentOperation
            || parameter.ContainingSymbol is not IMethodSymbol method
            || ResolveMethodBody(method, semanticModel) is not { } helper)
        {
            return null;
        }

        var callMap = argumentOperation.Parent is { } call
            ? BuildArgumentMap(call, outerMap)
            : outerMap;

        return (parameter, method, helper, callMap);
    }

    // The candidate writes no OTHER candidate definitely kills before the value is
    // observed -- the survivor filter every write-collecting scan applies.
    public static IEnumerable<SyntaxNode> Surviving(IReadOnlyCollection<SyntaxNode> candidates, SyntaxNode observedAt, ISymbol variable, SemanticModel semanticModel)
    {
        return candidates.Where(write =>
            !candidates.Any(other => other != write && DefinitelyOverwrites(other, write, observedAt, variable, semanticModel)));
    }

    // A write the flow can never reach -- one after an unconditional `return`/`throw` --
    // binds nothing the caller can observe, so it is no candidate and kills nothing.
    // Roslyn answers this exactly; anything it cannot analyse counts as reachable.
    public static bool IsReachable(SyntaxNode node, SemanticModel semanticModel)
    {
        var statement = node.FirstAncestorOrSelf<StatementSyntax>();
        if (statement is null || statement.Parent is not BlockSyntax || statement.SyntaxTree != semanticModel.SyntaxTree)
        {
            return true;
        }

        var flow = semanticModel.AnalyzeControlFlow(statement);
        return !flow.Succeeded || flow.StartPointIsReachable;
    }

    // A reachable node that can BIND the variable: a (possibly deconstructing) assignment
    // whose target writes it, or an out-argument handoff naming it (ref aliases included on
    // both). Member writes count only through the member's OWN receiver -- `other.patch =
    // safe;` on some other instance neither kills nor stands in for what a `this.patch`
    // read observes.
    public static bool IsVariableWriteNode(SyntaxNode node, ISymbol variable, SemanticModel semanticModel)
    {
        var writes = node switch
        {
            AssignmentExpressionSyntax assignment => ReassignmentTargets(assignment) is { } targets
                && targets.Any(target => WritesVariable(target, variable, semanticModel) && WritesThroughOwnReceiver(target, variable)),
            ArgumentSyntax argument => argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)
                && WritesVariable(argument.Expression, variable, semanticModel)
                && WritesThroughOwnReceiver(argument.Expression, variable),
            _ => false,
        };

        return writes && IsReachable(node, semanticModel);
    }

    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> OutHandoffNodeBodies(
        SyntaxNode node,
        ISymbol variable,
        SemanticModel semanticModel,
        HashSet<SyntaxNode> visited,
        Dictionary<IParameterSymbol, IOperation>? callMap)
    {
        // Callers hand in IsVariableWriteNode hits only, so the shape decides everything.
        switch (node)
        {
            case AssignmentExpressionSyntax assignment:
                foreach (var value in AssignedValuesFor(assignment.Left, assignment.Right, variable, semanticModel))
                {
                    foreach (var body in AssignedValueBodies(value, semanticModel, callMap))
                    {
                        yield return body;
                    }
                }

                break;
            case ArgumentSyntax forwarded:
                foreach (var body in OutHandoffBodies(forwarded, semanticModel, visited, callMap))
                {
                    yield return body;
                }

                break;
        }
    }

    // The call-site map resolves a value forwarded from ANOTHER delegate parameter
    // (`Provide(source, out patch)` with `p = source`), aliases included -- and a value
    // ASSEMBLED in place (`d = Make(v);` binds the factory's returns, `d = Step;` the
    // computed property's) resolves through the assembled-source chain with the same map.
    private static IEnumerable<(ComputationBody Body, Dictionary<IParameterSymbol, IOperation>? ArgumentMap)> AssignedValueBodies(
        IOperation value,
        SemanticModel? semanticModel,
        Dictionary<IParameterSymbol, IOperation>? callMap)
    {
        var resolved = ResolveDelegateValue(value, semanticModel, callMap);
        var resolvedAny = false;
        foreach (var body in OfArgumentValue(resolved, semanticModel))
        {
            resolvedAny = true;
            yield return (body, callMap);
        }

        if (resolvedAny)
        {
            yield break;
        }

        foreach (var entry in AssembledSourceBodies(resolved, semanticModel, callMap))
        {
            yield return entry;
        }
    }

    // "Straight-line" = the killer sits in plain statements between itself and wherever the
    // value is observed (an invoke site, or an out-helper's own scope end); a killer inside
    // an if/loop may not run, so it kills nothing -- and an exit statement between the two
    // writes can leave the earlier value observable, so nothing is definite past one. The
    // argument-list hop covers out-handoff killers; a conditional-access call stays rejected.
    public static bool DefinitelyOverwrites(SyntaxNode killer, SyntaxNode victim, SyntaxNode reference, ISymbol variable, SemanticModel semanticModel)
    {
        // Syntactic tests first: span order and the parent walk reject most candidate
        // pairs outright, while the write test issues semantic queries and the exit walk
        // covers a whole function.
        if (killer.SpanStart <= victim.SpanStart || killer.SpanStart >= reference.Span.End)
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

        return IsDeterminingWrite(killer, variable, semanticModel) && !ExitsBetween(victim, killer);
    }

    // A killer must fully DETERMINE the new value: a simple assignment -- deconstruction
    // included, which assigns every target slot -- or an `out` handoff (the language
    // requires the callee to assign before returning). `??=`/compound forms can keep the
    // old value and stay excluded.
    private static bool IsDeterminingWrite(SyntaxNode killer, ISymbol variable, SemanticModel semanticModel)
    {
        return killer switch
        {
            AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && ReassignmentTargets(assignment) is { } targets
                && targets.Any(target => WritesVariable(target, variable, semanticModel)),
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

    // The storage a ref local ALIASES, through `= ref` initializer chains: `ref int alias =
    // ref shared; alias++` writes shared. Null for anything but a ref local (the visited set
    // bounds error-code cycles). The operation-level twin of WritesVariable's alias walk.
    public static IOperation? RefAliasTarget(ILocalReferenceOperation reference)
    {
        IOperation? current = reference;
        HashSet<ISymbol>? visited = null;
        while (current is ILocalReferenceOperation { Local: { RefKind: RefKind.Ref } local } alias)
        {
            visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            current = visited.Add(local)
                && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                    is VariableDeclaratorSyntax { Initializer.Value: RefExpressionSyntax { Expression: { } referent } }
                ? alias.SemanticModel?.GetOperation(referent)
                : null;
        }

        return ReferenceEquals(current, reference) ? null : current;
    }

    // A method group (`f.CreateMemoizR(Compute)`) or local-function reference is as much a
    // computation as a lambda; its declaration is the body to analyze. Resolution is same-tree
    // by design: another tree's declarations have no operation model here, and the runtime
    // checks still cover what the analyzer cannot see.
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
        return Walk(root, FactoryMethods.IsComputationHost, pruneFunctions: false);
    }

    // The unpruned walk for STORED-closure facts (MZR004's capture verdicts): a nested
    // computation host's delegate is still BUILT by this body, so what it captures is pinned
    // in this body's display chain even though the nested invocation's own analysis covers it
    // for the mutation/Set rules. Only a nested OPTIMISTIC PATCH is skipped -- its own Apply
    // analysis repeats exactly this capture walk on the same operations.
    public static IEnumerable<IOperation> DescendStoredClosure(IOperation root)
    {
        return Walk(root, FactoryMethods.IsOptimisticPatchHost, pruneFunctions: false);
    }

    // Like Descend, but restricted to the operations the computation executes as part of its OWN
    // evaluation: nested anonymous functions and local-function declarations are pruned. Their
    // bodies run only if and when the delegate is invoked -- and MZR003's own fix guidance is to
    // BUILD such a callback ("schedule the write outside the evaluation"), which executes later
    // on a flow that holds no evaluation lock and so must not be flagged. (A nested function
    // the computation synchronously INVOKES is reached through the invoked-delegate
    // resolution instead.) MZR002 deliberately keeps the full walk: a captured-state write
    // is a data race whenever the callback runs, deferred or not.
    public static IEnumerable<IOperation> DescendDirectExecution(IOperation root)
    {
        return Walk(root, FactoryMethods.IsComputationHost, pruneFunctions: true);
    }

    // The depth-first walk behind the three: a nested host's arguments are walked only as far
    // as the outer computation EVALUATES them, and pruning drops nested functions altogether.
    private static IEnumerable<IOperation> Walk(IOperation root, Func<IMethodSymbol, bool> isNestedHost, bool pruneFunctions)
    {
        foreach (var child in root.ChildOperations)
        {
            if (pruneFunctions && child is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                continue;
            }

            yield return child;

            var below = child is IInvocationOperation invocation && isNestedHost(invocation.TargetMethod)
                ? invocation.Arguments.SelectMany(argument => NestedHostArgument(argument.Value, isNestedHost, pruneFunctions))
                : Walk(child, isNestedHost, pruneFunctions);

            foreach (var descendant in below)
            {
                yield return descendant;
            }
        }
    }

    // What the OUTER computation evaluates of a nested host's argument: an ordinary argument
    // entirely, and of a delegate argument its CONSTRUCTION -- a method group's receiver
    // (`CreateMemoizR(Get(++shared).Compute)` runs `Get` now), a params element built by a
    // call (`CreateConcurrentMap(Make(v.Set(1)))`). Only the delegate BODIES are deferred,
    // and those the nested invocation's own analysis covers.
    private static IEnumerable<IOperation> NestedHostArgument(IOperation value, Func<IMethodSymbol, bool> isNestedHost, bool pruneFunctions)
    {
        return Unwrap(value) switch
        {
            IDelegateCreationOperation { Target: IMethodReferenceOperation { Instance: { } receiver } }
                => Walk(receiver, isNestedHost, pruneFunctions).Prepend(receiver),
            IDelegateCreationOperation => Enumerable.Empty<IOperation>(),
            IArrayCreationOperation array => array.Initializer?.ElementValues
                .SelectMany(element => NestedHostArgument(element, isNestedHost, pruneFunctions))
                ?? Enumerable.Empty<IOperation>(),
            _ => Walk(value, isNestedHost, pruneFunctions).Prepend(value),
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
    public static bool CanExecuteBefore(SyntaxNode node, SyntaxNode reference, ISymbol variable, SemanticModel? semanticModel, HashSet<SyntaxNode>? visitedFunctions = null)
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

            if (IsFunctionBoundary(current))
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
            return visitedFunctions.Add(lambda) && ReferenceRunsBefore(lambda, reference, variable, semanticModel, visitedFunctions);
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

        return ReferencesTo(symbol, semanticModel).Any(identifier =>
            !local.Span.Contains(identifier.Span)
            && ReferenceRunsBefore(identifier, reference, variable, semanticModel, visitedFunctions));
    }

    // Every same-tree mention of the symbol that is a real reference: a name inside nameof()
    // is a compile-time string, which neither runs a function nor lets a delegate escape.
    private static IEnumerable<IdentifierNameSyntax> ReferencesTo(ISymbol symbol, SemanticModel semanticModel)
    {
        return semanticModel.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(name => name.Identifier.ValueText == symbol.Name
                && !IsInsideNameOfSyntax(name)
                && SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(name).Symbol, symbol));
    }

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

    // A direct CALL (or an immediately-invoked lambda) runs the function at the call's own
    // position. A method-group or lambda LIFT runs it wherever the receiving delegate
    // variable is invoked -- so those invocation sites become the ordering points; a lift
    // that escapes anywhere else (an argument, a return value) stays unknowable. Casts and
    // parentheses change neither: `(Action)Rebind` lifted into a variable is still ordered
    // by that variable's invocation sites.
    private static bool ReferenceRunsBefore(SyntaxNode use, SyntaxNode reference, ISymbol variable, SemanticModel semanticModel, HashSet<SyntaxNode> visitedFunctions)
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
        return ReferencesTo(lifted, semanticModel).Any(name => LiftedUseRunsBefore(name, reference, variable, semanticModel, visitedFunctions));
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
    // constructor; or a user-defined operator/conversion. (A member mentioned only in nameof
    // is not executed -- callers guard.)
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
            case ICompoundAssignmentOperation { OperatorMethod: { } compoundOperator }:
                yield return compoundOperator;
                break;
            case IIncrementOrDecrementOperation { OperatorMethod: { } incrementOperator }:
                yield return incrementOperator;
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
        return value is null ? null : Unwrap(value).Type;
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
