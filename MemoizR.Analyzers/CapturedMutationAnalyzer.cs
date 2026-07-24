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
        var walk = new ChaseState();
        InspectComputationOperations(context, computation, computation.Scope, semanticModel, walk, argumentMap: null);
    }

    // Per-computation walk state: the visited sets bound helper/delegate cycles, and the
    // reported set keeps a body re-walked under a DIFFERENT delegate binding from emitting
    // the same mutation diagnostic twice.
    private sealed class ChaseState
    {
        public readonly HashSet<(IMethodSymbol, ImmutableArray<IParameterSymbol>, bool, string)> VisitedHelpers = new(ChaseKeyComparer.Instance);
        public readonly HashSet<SyntaxNode> VisitedDelegates = new();
        public readonly HashSet<SyntaxNode> ReportedTargets = new();
    }

    private static void InspectComputationOperations(
        OperationAnalysisContext context,
        ComputationLambdas.ComputationBody body,
        SyntaxNode computationScope,
        SemanticModel? semanticModel,
        ChaseState walk,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        ImmutableArray<IParameterSymbol> thisParameters = default,
        bool foreignThis = false)
    {
        foreach (var operation in ComputationLambdas.Descend(body.Body))
        {
            foreach (var target in MutationTargets(operation))
            {
                ReportIfShared(context, target, body.Scope, computationScope, thisParameters, foreignThis, walk.ReportedTargets);
            }

            InspectCalledHelper(context, operation, computationScope, semanticModel, walk, thisParameters, foreignThis, argumentMap);
            InspectInvokedDelegate(context, operation, computationScope, semanticModel, walk, argumentMap, thisParameters, foreignThis);
        }
    }

    // A delegate the computation synchronously INVOKES runs its body on the computation's
    // flows: a captured lambda's write (`Func<int,int> d = x => { applied++; return x; }`
    // invoked by the body) races exactly like inline code.
    private static void InspectInvokedDelegate(
        OperationAnalysisContext context,
        IOperation operation,
        SyntaxNode computationScope,
        SemanticModel? semanticModel,
        ChaseState walk,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        ImmutableArray<IParameterSymbol> thisParameters,
        bool foreignThis)
    {
        if (operation is not IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke } invoke)
        {
            return;
        }

        foreach (var (delegateBody, map) in ComputationLambdas.InvokedDelegateBodies(invoke, semanticModel, argumentMap))
        {
            // The invoke's arguments bind the delegate's parameters exactly like a helper
            // call's: one handed the enclosing instance IS `this` for the walked body.
            InspectDelegateBody(context, delegateBody, computationScope, semanticModel, walk, map, BoundThisParameters(map, thisParameters, foreignThis), foreignThis);
        }
    }

    private static ImmutableArray<IParameterSymbol> BoundThisParameters(
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        ImmutableArray<IParameterSymbol> thisParameters,
        bool foreignThis)
    {
        if (argumentMap is null)
        {
            return thisParameters;
        }

        var bound = argumentMap
            .Where(entry => IsComputationInstance(ComputationLambdas.Unwrap(entry.Value), thisParameters, foreignThis))
            .Select(entry => entry.Key)
            .ToImmutableArray();

        return bound.IsEmpty ? thisParameters : bound;
    }

    private static void InspectDelegateBody(
        OperationAnalysisContext context,
        ComputationLambdas.ComputationBody delegateBody,
        SyntaxNode computationScope,
        SemanticModel? semanticModel,
        ChaseState walk,
        Dictionary<IParameterSymbol, IOperation>? argumentMap,
        ImmutableArray<IParameterSymbol> thisParameters,
        bool foreignThis)
    {
        if (!computationScope.Span.Contains(delegateBody.Scope.Span) && walk.VisitedDelegates.Add(delegateBody.Scope))
        {
            InspectComputationOperations(context, delegateBody, computationScope, semanticModel, walk, argumentMap, thisParameters, foreignThis);
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
        ChaseState walk,
        ImmutableArray<IParameterSymbol> thisParameters,
        bool foreignThis,
        Dictionary<IParameterSymbol, IOperation>? argumentMap)
    {
        foreach (var method in ChaseableMethods(operation, thisParameters, foreignThis))
        {
            if (ComputationLambdas.IsInsideNameOf(operation)
                || (method.MethodKind == MethodKind.LocalFunction && !ComputationLambdas.IsDeclaredOutside(method, computationScope))
                || ComputationLambdas.ResolveMethodBody(method, semanticModel) is not { } helper)
            {
                continue;
            }

            var nestedThis = NestedThisParameter(operation, thisParameters, foreignThis);
            var nestedForeign = ForeignReceiver(operation, thisParameters, foreignThis);

            // The map lets a delegate handed INTO the helper resolve at its invocation
            // (`Run(step)` with `Run(Func<int> f) => f()` executes step's body). Only the
            // DELEGATE bindings key the visited set: they decide what an invoked parameter
            // resolves to, while re-walking on ordinary arguments would only repeat the
            // same reports (which the reported set deduplicates anyway).
            var nestedMap = ComputationLambdas.BuildArgumentMap(operation, argumentMap);
            if (!walk.VisitedHelpers.Add((method, nestedThis, nestedForeign, DelegateBindingsKey(nestedMap))))
            {
                continue;
            }

            InspectComputationOperations(context, helper, computationScope, semanticModel, walk, nestedMap, nestedThis, nestedForeign);
        }
    }

    private static string DelegateBindingsKey(Dictionary<IParameterSymbol, IOperation>? map)
    {
        if (map is null)
        {
            return "";
        }

        var delegates = map.Where(entry => entry.Key.Type.TypeKind == TypeKind.Delegate)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        return ComputationLambdas.ArgumentMapKey(delegates);
    }

    private static IEnumerable<IMethodSymbol> ChaseableMethods(IOperation operation, ImmutableArray<IParameterSymbol> thisParameters, bool foreignThis)
    {
        switch (operation)
        {
            case IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } reference:
                yield return reference.Method;
                break;
            case IPropertyReferenceOperation property:
                foreach (var accessor in ChaseableAccessors(property, thisParameters, foreignThis))
                {
                    yield return accessor;
                }

                break;

            // Every other executed shape runs like a call: a same-tree CONSTRUCTOR
            // (`new Writer()` incrementing a static counter), a user-defined
            // operator/conversion, a custom event accessor, using-driven Dispose.
            // ForeignReceiver marks a constructor's fresh object so its own instance
            // writes stay suppressed while statics and captured state still report.
            default:
                foreach (var method in ComputationLambdas.ExecutedMethods(operation))
                {
                    yield return method;
                }

                break;
        }
    }

    // A property READ runs its getter like an invoked helper, on any receiver. A WRITE on
    // the computation's own instance (or a static) needs no chase -- the property itself is
    // the mutation target the direct walk reports -- but on a FOREIGN receiver nothing else
    // looks at the setter body, whose side effects (`set { hits++; }`) still run on every
    // replay.
    private static IEnumerable<IMethodSymbol> ChaseableAccessors(IPropertyReferenceOperation property, ImmutableArray<IParameterSymbol> thisParameters, bool foreignThis)
    {
        var (reads, writes) = ComputationLambdas.PropertyUsage(property);
        if (reads && property.Property.GetMethod is { } getter)
        {
            yield return getter;
        }

        if (writes && property.Property.SetMethod is { } setter
            && property.Instance is { } instance && !IsComputationInstance(instance, thisParameters, foreignThis))
        {
            yield return setter;
        }
    }

    // Whether the chased body's own `this` is some OTHER object than the computation's:
    // true when the receiver is neither the enclosing instance nor the current this-bound
    // parameter. A receiverless callee (static, extension, local function) has no `this` of
    // its own to rebind, so the current answer carries through -- a local function nested
    // in a foreign body still belongs to that foreign object.
    private static bool ForeignReceiver(IOperation operation, ImmutableArray<IParameterSymbol> thisParameters, bool foreignThis)
    {
        // A constructor body's `this` is the FRESH object being built, and a using
        // resource's Dispose runs on that resource -- never on the computation's instance,
        // whichever flow reaches them. Their own instance writes are per-evaluation
        // storage, so only the statics and captures inside them are shared.
        if (operation is IObjectCreationOperation or IUsingOperation or IUsingDeclarationOperation)
        {
            return true;
        }

        var instance = operation switch
        {
            IInvocationOperation call => call.Instance,
            IPropertyReferenceOperation property => property.Instance,
            _ => null,
        };

        return instance is null ? foreignThis : !IsComputationInstance(instance, thisParameters, foreignThis);
    }

    // The `this` identity for a chased body: the parameter that RECEIVES the enclosing
    // instance at this call -- an extension's receiver argument (`this.Inc()` with
    // `Inc(this C c)`) or an explicit ordinary argument (`Mutate(this)`) -- resolved
    // through the current body's own binding so chains keep it. A call handing the
    // instance nowhere rebinds nothing: a chased local function keeps the current binding
    // (its closure can still name an extension body's receiver parameter), while any other
    // callee cannot reference it at all.
    private static ImmutableArray<IParameterSymbol> NestedThisParameter(IOperation operation, ImmutableArray<IParameterSymbol> thisParameters, bool foreignThis)
    {
        if (operation is not IInvocationOperation call)
        {
            return thisParameters;
        }

        // EVERY parameter the instance was handed to: `Mutate(this, this)` binds both, and a
        // write through either lands on the computation's object.
        var bound = call.Arguments
            .Where(argument => argument.Parameter is not null
                && IsComputationInstance(ComputationLambdas.Unwrap(argument.Value), thisParameters, foreignThis))
            .Select(argument => argument.Parameter!)
            .ToImmutableArray();

        if (!bound.IsEmpty)
        {
            return bound;
        }

        return call.TargetMethod.MethodKind == MethodKind.LocalFunction ? thisParameters : default;
    }

    // The operation refers to the COMPUTATION's enclosing instance: a direct `this` -- only
    // while walking code whose `this` IS that instance -- the current body's this-bound
    // parameter, or a local ALIAS resolving to either through its same-tree initializer
    // chain (`var alias = c; Mutate(alias);` hands the instance on). An alias written
    // before its use no longer proves the binding and stays untrusted.
    private static bool IsComputationInstance(IOperation? receiver, ImmutableArray<IParameterSymbol> thisParameters, bool foreignThis)
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
                // ...unless the helper rebound it first (`c = new C(); c.Counter++`), in
                // which case the write lands on whatever it was rebound to.
                case IParameterReferenceOperation parameter when !thisParameters.IsDefaultOrEmpty
                    && thisParameters.Contains<ISymbol>(parameter.Parameter, SymbolEqualityComparer.Default):
                    return !ComputationLambdas.IsWrittenBefore(parameter.Parameter, current.Syntax, current.SemanticModel);
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

    private sealed class ChaseKeyComparer : IEqualityComparer<(IMethodSymbol Method, ImmutableArray<IParameterSymbol> This, bool Foreign, string Bindings)>
    {
        public static readonly ChaseKeyComparer Instance = new();

        public bool Equals((IMethodSymbol Method, ImmutableArray<IParameterSymbol> This, bool Foreign, string Bindings) x, (IMethodSymbol Method, ImmutableArray<IParameterSymbol> This, bool Foreign, string Bindings) y)
        {
            return SymbolEqualityComparer.Default.Equals(x.Method, y.Method)
                && x.This.IsDefaultOrEmpty == y.This.IsDefaultOrEmpty
                && (x.This.IsDefaultOrEmpty || Enumerable.SequenceEqual(x.This, y.This, SymbolEqualityComparer.Default))
                && x.Foreign == y.Foreign
                && x.Bindings == y.Bindings;
        }

        public int GetHashCode((IMethodSymbol Method, ImmutableArray<IParameterSymbol> This, bool Foreign, string Bindings) key)
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

    private static void ReportIfShared(OperationAnalysisContext context, IOperation target, SyntaxNode scope, SyntaxNode computationScope, ImmutableArray<IParameterSymbol> thisParameters, bool foreignThis, HashSet<SyntaxNode> reportedTargets)
    {
        var (kind, symbol) = ResolveSharedRoot(target, scope, thisParameters, foreignThis);
        if (kind is null || !SharesComputationEnvironment(symbol!, computationScope) || !reportedTargets.Add(target.Syntax))
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
    private static (string? kind, ISymbol? symbol) ResolveSharedRoot(IOperation target, SyntaxNode scope, ImmutableArray<IParameterSymbol> thisParameters, bool foreignThis)
    {
        switch (target)
        {
            case ILocalReferenceOperation local when ComputationLambdas.IsDeclaredOutside(local.Local, scope):
                return ("captured local", local.Local);
            case IParameterReferenceOperation parameter when ComputationLambdas.IsDeclaredOutside(parameter.Parameter, scope):
                return ("captured parameter", parameter.Parameter);
            case IFieldReferenceOperation { Field.IsStatic: true } staticField:
                return ("static field", staticField.Field);
            case IFieldReferenceOperation field:
                return ResolveThroughReceiver(field.Instance, "field", field.Field, scope, thisParameters, foreignThis);
            case IPropertyReferenceOperation { Property.IsStatic: true } staticProperty:
                return ("static property", staticProperty.Property);
            case IPropertyReferenceOperation property:
                return ResolveThroughReceiver(property.Instance, "property", property.Property, scope, thisParameters, foreignThis);
            case IEventReferenceOperation { Event.IsStatic: true } staticEvent:
                return ("static event", staticEvent.Event);
            case IEventReferenceOperation @event:
                return ResolveThroughReceiver(@event.Instance, "event", @event.Event, scope, thisParameters, foreignThis);
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
    private static (string? kind, ISymbol? symbol) ResolveThroughReceiver(IOperation? receiver, string memberKind, ISymbol member, SyntaxNode scope, ImmutableArray<IParameterSymbol> thisParameters, bool foreignThis)
    {
        switch (receiver)
        {
            case var enclosing when IsComputationInstance(enclosing, thisParameters, foreignThis):
                return (memberKind, member);
            case ILocalReferenceOperation { Local.Type.IsValueType: true } local when ComputationLambdas.IsDeclaredOutside(local.Local, scope):
                return ("captured local", local.Local);
            case IParameterReferenceOperation { Parameter.Type.IsValueType: true } parameter when ComputationLambdas.IsDeclaredOutside(parameter.Parameter, scope):
                return ("captured parameter", parameter.Parameter);
            case IFieldReferenceOperation { Type.IsValueType: true } outerField:
                return ResolveSharedRoot(outerField, scope, thisParameters, foreignThis);
            case IPropertyReferenceOperation { Type.IsValueType: true } outerProperty:
                return ResolveSharedRoot(outerProperty, scope, thisParameters, foreignThis);
            default:
                return (null, null);
        }
    }

}
