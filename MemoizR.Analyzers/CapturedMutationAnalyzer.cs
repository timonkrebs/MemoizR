using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MemoizR.Analyzers;

// MZR002, the SE-0412 analog: a reactive computation that WRITES state it shares with code
// outside itself (a captured local/parameter, a field of the enclosing object, a static field)
// is unsynchronized shared mutation -- computations run concurrently on other flows. Reads are
// deliberately not flagged: read-only captured configuration is idiomatic, and proving a read
// races requires whole-program knowledge an analyzer does not have. Mutations through a captured
// reference (`capturedList.Add(1)`) are likewise out of scope here; that is MZR001's territory
// (the value type should be Sendable). The suggested fix is the library's own model: lift the
// state into a Signal/EagerRelativeSignal.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapturedMutationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.CapturedMutation);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!FactoryMethods.IsComputationHost(invocation.TargetMethod))
        {
            return;
        }

        foreach (var computation in ComputationLambdas.OfInvocation(invocation))
        {
            InspectComputationBody(context, computation, invocation.SemanticModel);
        }

        // Patch shapes resolved beyond plain arguments -- assembled by an out-helper,
        // returned by a computed delegate property or a same-tree factory -- replay on the
        // state's flows exactly like an inline patch, so their writes race identically.
        if (FactoryMethods.IsOptimisticPatchHost(invocation.TargetMethod))
        {
            foreach (var argument in invocation.Arguments)
            {
                if (argument.Parameter?.Type is not { TypeKind: TypeKind.Delegate })
                {
                    continue;
                }

                foreach (var (body, _) in ComputationLambdas.AssembledPatchBodies(argument.Value, invocation.SemanticModel))
                {
                    InspectComputationBody(context, body, invocation.SemanticModel);
                }
            }
        }
    }

    private static void InspectComputationBody(OperationAnalysisContext context, ComputationLambdas.ComputationBody computation, SemanticModel? semanticModel)
    {
        var visitedHelpers = new HashSet<(IMethodSymbol, IParameterSymbol?, bool)>(ChaseKeyComparer.Instance);
        var visitedDelegates = new HashSet<SyntaxNode>();
        InspectComputationOperations(context, computation, computation.Scope, semanticModel, visitedHelpers, visitedDelegates);
    }

    private static void InspectComputationOperations(
        OperationAnalysisContext context,
        ComputationLambdas.ComputationBody body,
        SyntaxNode computationScope,
        SemanticModel? semanticModel,
        HashSet<(IMethodSymbol, IParameterSymbol?, bool)> visitedHelpers,
        HashSet<SyntaxNode> visitedDelegates)
    {
        foreach (var operation in ComputationLambdas.Descend(body.Body))
        {
            foreach (var target in MutationTargets(operation))
            {
                ReportIfShared(context, target, body.Scope, computationScope, thisParameter: null, foreignThis: false);
            }

            InspectCalledHelper(context, operation, computationScope, semanticModel, visitedHelpers, thisParameter: null, foreignThis: false);
            InspectInvokedDelegate(context, operation, computationScope, semanticModel, visitedHelpers, visitedDelegates);
        }
    }

    // A delegate the computation synchronously INVOKES runs its body on the computation's
    // flows: a captured lambda's write (`Func<int,int> d = x => { applied++; ... }` invoked
    // by the body) races exactly like inline code. Only bodies declared OUTSIDE the
    // computation need this chase -- one declared inside is already covered by the full
    // walk above; the visited set bounds self-invoking delegates.
    private static void InspectInvokedDelegate(
        OperationAnalysisContext context,
        IOperation operation,
        SyntaxNode computationScope,
        SemanticModel? semanticModel,
        HashSet<(IMethodSymbol, IParameterSymbol?, bool)> visitedHelpers,
        HashSet<SyntaxNode> visitedDelegates)
    {
        if (operation is not IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke, Instance: { } callee })
        {
            return;
        }

        var resolved = ComputationLambdas.ResolveDelegateValue(callee, semanticModel, null);
        foreach (var delegateBody in ComputationLambdas.OfArgumentValue(resolved, semanticModel))
        {
            if (!computationScope.Span.Contains(delegateBody.Scope.Span) && visitedDelegates.Add(delegateBody.Scope))
            {
                InspectComputationOperations(context, delegateBody, computationScope, semanticModel, visitedHelpers, visitedDelegates);
            }
        }
    }

    // A helper the computation runs writes the same state the inline form would: a LOCAL
    // FUNCTION's closure IS the computation's environment (`int Next() { applied++; ... }`),
    // a method invoked on THIS or statically (`void Inc() => counter++;`) mutates the
    // enclosing object/static state on every run, and a GETTER read executes the same way
    // (`int P { get { counter++; ... } }`) -- shapes MZR004 cannot carry (a Sendable type or
    // int hides them; the WRITE is the race). A helper handed the enclosing instance -- an
    // extension's receiver argument, or an explicit `Mutate(this)` -- is the same in
    // disguise: the receiving parameter IS `this` for the walked body, so it is carried as
    // such. A helper on some OTHER receiver is chased too -- a STATIC it writes races
    // regardless of who the receiver is -- but with `foreignThis` set, so writes to that
    // body's own object stay suppressed: those are a captured-reference mutation,
    // deliberately MZR001's territory. Bodies resolve same-tree; the visited set -- keyed
    // per RECEIVER BINDING, so `other.Inc()` before `this.Inc()` cannot poison the chase --
    // bounds call cycles, and the helper's own per-call locals stay exempt via the
    // enclosing-function guard in ReportIfShared.
    private static void InspectCalledHelper(
        OperationAnalysisContext context,
        IOperation operation,
        SyntaxNode computationScope,
        SemanticModel? semanticModel,
        HashSet<(IMethodSymbol, IParameterSymbol?, bool)> visited,
        IParameterSymbol? thisParameter,
        bool foreignThis)
    {
        foreach (var method in ChaseableMethods(operation, thisParameter, foreignThis))
        {
            if (ComputationLambdas.IsInsideNameOf(operation)
                || (method.MethodKind == MethodKind.LocalFunction && !ComputationLambdas.IsDeclaredOutside(method, computationScope))
                || ComputationLambdas.ResolveMethodBody(method, semanticModel) is not { } helper)
            {
                continue;
            }

            var nestedThis = NestedThisParameter(operation, thisParameter, foreignThis);
            var nestedForeign = ForeignReceiver(operation, thisParameter, foreignThis);
            if (!visited.Add((method, nestedThis, nestedForeign)))
            {
                continue;
            }

            foreach (var inner in ComputationLambdas.Descend(helper.Body))
            {
                foreach (var target in MutationTargets(inner))
                {
                    ReportIfShared(context, target, helper.Scope, computationScope, nestedThis, nestedForeign);
                }

                InspectCalledHelper(context, inner, computationScope, semanticModel, visited, nestedThis, nestedForeign);
            }
        }
    }

    private static IEnumerable<IMethodSymbol> ChaseableMethods(IOperation operation, IParameterSymbol? thisParameter, bool foreignThis)
    {
        switch (operation)
        {
            case IInvocationOperation call:
                yield return call.TargetMethod;
                break;
            case IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } reference:
                yield return reference.Method;
                break;
            case IPropertyReferenceOperation property:
                foreach (var accessor in ChaseableAccessors(property, thisParameter, foreignThis))
                {
                    yield return accessor;
                }

                break;
        }
    }

    // A property READ runs its getter like an invoked helper, on any receiver. A WRITE on
    // the computation's own instance (or a static) needs no chase -- the property itself is
    // the mutation target the direct walk reports -- but on a FOREIGN receiver nothing else
    // looks at the setter body, whose side effects (`set { hits++; }`) still run on every
    // replay.
    private static IEnumerable<IMethodSymbol> ChaseableAccessors(IPropertyReferenceOperation property, IParameterSymbol? thisParameter, bool foreignThis)
    {
        var (reads, writes) = ComputationLambdas.PropertyUsage(property);
        if (reads && property.Property.GetMethod is { } getter)
        {
            yield return getter;
        }

        if (writes && property.Property.SetMethod is { } setter
            && property.Instance is { } instance && !IsComputationInstance(instance, thisParameter, foreignThis))
        {
            yield return setter;
        }
    }

    // Whether the chased body's own `this` is some OTHER object than the computation's:
    // true when the receiver is neither the enclosing instance nor the current this-bound
    // parameter. A receiverless callee (static, extension, local function) has no `this` of
    // its own to rebind, so the current answer carries through -- a local function nested
    // in a foreign body still belongs to that foreign object.
    private static bool ForeignReceiver(IOperation operation, IParameterSymbol? thisParameter, bool foreignThis)
    {
        var instance = operation switch
        {
            IInvocationOperation call => call.Instance,
            IPropertyReferenceOperation property => property.Instance,
            _ => null,
        };

        return instance is null ? foreignThis : !IsComputationInstance(instance, thisParameter, foreignThis);
    }

    // The `this` identity for a chased body: the parameter that RECEIVES the enclosing
    // instance at this call -- an extension's receiver argument (`this.Inc()` with
    // `Inc(this C c)`) or an explicit ordinary argument (`Mutate(this)`) -- resolved
    // through the current body's own binding so chains keep it. A call handing the
    // instance nowhere rebinds nothing: a chased local function keeps the current binding
    // (its closure can still name an extension body's receiver parameter), while any other
    // callee cannot reference it at all.
    private static IParameterSymbol? NestedThisParameter(IOperation operation, IParameterSymbol? thisParameter, bool foreignThis)
    {
        if (operation is not IInvocationOperation call)
        {
            return thisParameter;
        }

        foreach (var argument in call.Arguments)
        {
            var value = argument.Value;
            while (value is IConversionOperation conversion)
            {
                value = conversion.Operand;
            }

            if (IsComputationInstance(value, thisParameter, foreignThis) && argument.Parameter is { } parameter)
            {
                return parameter;
            }
        }

        return call.TargetMethod.MethodKind == MethodKind.LocalFunction ? thisParameter : null;
    }

    // The operation refers to the COMPUTATION's enclosing instance: a direct `this` -- only
    // while walking code whose `this` IS that instance -- the current body's this-bound
    // parameter, or a local ALIAS resolving to either through its same-tree initializer
    // chain (`var alias = c; Mutate(alias);` hands the instance on). An alias written
    // before its use no longer proves the binding and stays untrusted.
    private static bool IsComputationInstance(IOperation? receiver, IParameterSymbol? thisParameter, bool foreignThis)
    {
        var current = receiver;
        HashSet<ISymbol>? visited = null;
        while (true)
        {
            switch (current)
            {
                case IConversionOperation conversion:
                    current = conversion.Operand;
                    continue;
                case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }:
                    return !foreignThis;
                case IParameterReferenceOperation parameter when thisParameter is not null
                    && SymbolEqualityComparer.Default.Equals(parameter.Parameter, thisParameter):
                    return true;
                case ILocalReferenceOperation local:
                    visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    if (!visited.Add(local.Local)
                        || ComputationLambdas.IsWrittenBefore(local.Local, current.Syntax, current.SemanticModel)
                        || ComputationLambdas.SameTreeInitializerOperation(local.Local, current.SemanticModel) is not { } initializer)
                    {
                        return false;
                    }

                    current = initializer;
                    continue;
                default:
                    return false;
            }
        }
    }

    private sealed class ChaseKeyComparer : IEqualityComparer<(IMethodSymbol Method, IParameterSymbol? This, bool Foreign)>
    {
        public static readonly ChaseKeyComparer Instance = new();

        public bool Equals((IMethodSymbol Method, IParameterSymbol? This, bool Foreign) x, (IMethodSymbol Method, IParameterSymbol? This, bool Foreign) y)
        {
            return SymbolEqualityComparer.Default.Equals(x.Method, y.Method)
                && SymbolEqualityComparer.Default.Equals(x.This, y.This)
                && x.Foreign == y.Foreign;
        }

        public int GetHashCode((IMethodSymbol Method, IParameterSymbol? This, bool Foreign) key)
        {
            return SymbolEqualityComparer.Default.GetHashCode(key.Method);
        }
    }

    private static ImmutableArray<IOperation> MutationTargets(IOperation operation)
    {
        switch (operation)
        {
            case ISimpleAssignmentOperation assignment:
                return ImmutableArray.Create(assignment.Target);
            case ICompoundAssignmentOperation compound:
                return ImmutableArray.Create(compound.Target);
            case ICoalesceAssignmentOperation coalesce:
                return ImmutableArray.Create(coalesce.Target);
            case IIncrementOrDecrementOperation increment:
                return ImmutableArray.Create(increment.Target);
            case IDeconstructionAssignmentOperation deconstruction when deconstruction.Target is ITupleOperation tuple:
                return FlattenTupleElements(tuple);
            case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument:
                return ImmutableArray.Create(argument.Value);
            case IEventAssignmentOperation eventAssignment:
                // `this.Changed += h` / `StaticEvent += h` mutates the event's backing delegate
                // on the enclosing/static object -- shared mutable state, like a field write.
                return ImmutableArray.Create(eventAssignment.EventReference);
            case IInvocationOperation call when IsMutatingValueReceiverCall(call):
                return ImmutableArray.Create(call.Instance!);
            default:
                return ImmutableArray<IOperation>.Empty;
        }
    }

    // A non-readonly instance method on a value-type receiver can write the receiver's storage
    // in place: `counter.Increment()` mutates the captured local exactly like `counter.Value++`.
    // Exempt: readonly members and readonly structs (most BCL value types -- int, DateTime,
    // Guid...), the object/ValueType virtuals (ToString/Equals/GetHashCode overrides are
    // overwhelmingly pure), and receivers the call cannot reach in place -- property receivers
    // and DEFENSIVE-COPY receivers (a readonly field, an `in` parameter, a ref-readonly local:
    // C# invokes non-readonly members on a copy there, so shared storage is never written).
    private static bool IsMutatingValueReceiverCall(IInvocationOperation call)
    {
        if (call.Instance is not { Type.IsValueType: true } receiver || IsCopyReceiver(receiver))
        {
            return false;
        }

        var method = call.TargetMethod;
        if (method.IsReadOnly || IsObjectVirtual(method.ContainingType) || IsObjectVirtual(method.OverriddenMethod?.ContainingType))
        {
            return false;
        }

        return true;
    }

    private static bool IsCopyReceiver(IOperation receiver)
    {
        return receiver switch
        {
            // A ref-RETURNING property hands out the storage itself, so a mutating call writes
            // shared state; an ordinary (or ref-readonly) getter hands out a copy, and direct
            // writes through those are already compiler errors.
            IPropertyReferenceOperation property => property.Property.RefKind != RefKind.Ref,
            IFieldReferenceOperation field => field.Field.IsReadOnly,
            IParameterReferenceOperation parameter => parameter.Parameter.RefKind == RefKind.In,
            ILocalReferenceOperation local => local.Local.RefKind == RefKind.RefReadOnly,
            _ => false,
        };
    }

    private static bool IsObjectVirtual(ITypeSymbol? declaringType)
    {
        return declaringType?.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;
    }

    // A deconstruction target can nest tuples arbitrarily -- `(a, (b, c)) = value` -- and every
    // leaf is written, so each nested ITupleOperation is flattened to its element targets.
    private static ImmutableArray<IOperation> FlattenTupleElements(ITupleOperation tuple)
    {
        var targets = ImmutableArray.CreateBuilder<IOperation>(tuple.Elements.Length);
        foreach (var element in tuple.Elements)
        {
            if (element is ITupleOperation nested)
            {
                targets.AddRange(FlattenTupleElements(nested));
            }
            else
            {
                targets.Add(element);
            }
        }

        return targets.ToImmutable();
    }

    private static void ReportIfShared(OperationAnalysisContext context, IOperation target, SyntaxNode scope, SyntaxNode computationScope, IParameterSymbol? thisParameter, bool foreignThis)
    {
        var (kind, symbol) = ResolveSharedRoot(target, scope, thisParameter, foreignThis);
        if (kind is null || !SharesComputationEnvironment(symbol!, computationScope))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.CapturedMutation,
            target.Syntax.GetLocation(),
            kind,
            symbol!.Name));
    }

    // A captured local/parameter is the computation's business only when it lives in a
    // function ENCLOSING the computation -- a chased helper's own locals are recreated per
    // call. Members of `this` and statics are shared storage wherever they are written from.
    // (For the computation's own body, scope == computationScope and C# scoping makes this
    // trivially true.)
    private static bool SharesComputationEnvironment(ISymbol symbol, SyntaxNode computationScope)
    {
        return symbol is not (ILocalSymbol or IParameterSymbol)
            || ComputationLambdas.DeclaredInFunctionEnclosing(symbol, computationScope);
    }

    // Resolves a mutation target to the shared storage it ultimately writes, walking up value-type
    // member receivers so that `this.s.Value++`, `StaticStruct.Value++`, and `capturedStruct.Value++`
    // all resolve to the shared field/local being mutated. Returns (null, null) when the target is a
    // member of some OTHER reference object (mutation through a captured reference -- MZR001's
    // territory, the value type there should be Sendable).
    private static (string? kind, ISymbol? symbol) ResolveSharedRoot(IOperation target, SyntaxNode scope, IParameterSymbol? thisParameter, bool foreignThis)
    {
        switch (target)
        {
            case ILocalReferenceOperation local when IsDeclaredOutside(local.Local, scope):
                return ("captured local", local.Local);
            case IParameterReferenceOperation parameter when IsDeclaredOutside(parameter.Parameter, scope):
                return ("captured parameter", parameter.Parameter);
            case IFieldReferenceOperation { Field.IsStatic: true } staticField:
                return ("static field", staticField.Field);
            case IFieldReferenceOperation field:
                return ResolveThroughReceiver(field.Instance, "field", field.Field, scope, thisParameter, foreignThis);
            case IPropertyReferenceOperation { Property.IsStatic: true } staticProperty:
                return ("static property", staticProperty.Property);
            case IPropertyReferenceOperation property:
                return ResolveThroughReceiver(property.Instance, "property", property.Property, scope, thisParameter, foreignThis);
            case IEventReferenceOperation { Event.IsStatic: true } staticEvent:
                return ("static event", staticEvent.Event);
            case IEventReferenceOperation @event:
                return ResolveThroughReceiver(@event.Instance, "event", @event.Event, scope, thisParameter, foreignThis);
            default:
                return (null, null);
        }
    }

    // A member on the COMPUTATION's instance -- direct `this` in a non-foreign body, or a
    // this-bound receiver parameter -- is reported as the member. A member on a value-type
    // receiver that is itself shared storage (a captured struct local/parameter, or a
    // nested value-type field/property/this) reports that RECEIVER -- mutating the member
    // mutates the receiver's storage. A member on any other receiver -- including a foreign
    // body's own `this` -- is left to MZR001.
    private static (string? kind, ISymbol? symbol) ResolveThroughReceiver(IOperation? receiver, string memberKind, ISymbol member, SyntaxNode scope, IParameterSymbol? thisParameter, bool foreignThis)
    {
        switch (receiver)
        {
            case var enclosing when IsComputationInstance(enclosing, thisParameter, foreignThis):
                return (memberKind, member);
            case ILocalReferenceOperation { Local.Type.IsValueType: true } local when IsDeclaredOutside(local.Local, scope):
                return ("captured local", local.Local);
            case IParameterReferenceOperation { Parameter.Type.IsValueType: true } parameter when IsDeclaredOutside(parameter.Parameter, scope):
                return ("captured parameter", parameter.Parameter);
            case IFieldReferenceOperation { Type.IsValueType: true } outerField:
                return ResolveSharedRoot(outerField, scope, thisParameter, foreignThis);
            case IPropertyReferenceOperation { Type.IsValueType: true } outerProperty:
                return ResolveSharedRoot(outerProperty, scope, thisParameter, foreignThis);
            default:
                return (null, null);
        }
    }

    // Shared with MZR004; see ComputationLambdas.IsDeclaredOutside for the span-test rationale.
    private static bool IsDeclaredOutside(ISymbol symbol, SyntaxNode scope)
    {
        return ComputationLambdas.IsDeclaredOutside(symbol, scope);
    }
}
