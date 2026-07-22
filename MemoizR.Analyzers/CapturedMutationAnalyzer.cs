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
            var visitedLocalFunctions = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var operation in ComputationLambdas.Descend(computation.Body))
            {
                foreach (var target in MutationTargets(operation))
                {
                    ReportIfShared(context, target, computation.Scope, computation.Scope);
                }

                InspectCalledLocalFunction(context, operation, computation.Scope, invocation.SemanticModel, visitedLocalFunctions);
            }
        }
    }

    // A LOCAL FUNCTION the computation invokes (or lifts into a delegate) has no receiver: its
    // closure IS the computation's environment, so `int Next() { applied++; return applied; }`
    // declared outside the computation writes state the computation shares exactly like an
    // inline `applied++` -- and MZR004 cannot carry this shape (the int is Sendable; the WRITE
    // is the race). Bodies resolve same-tree, the visited set bounds call cycles, and the
    // helper's own per-call locals stay exempt via the enclosing-function guard in
    // ReportIfShared.
    private static void InspectCalledLocalFunction(
        OperationAnalysisContext context,
        IOperation operation,
        SyntaxNode computationScope,
        SemanticModel? semanticModel,
        HashSet<IMethodSymbol> visited)
    {
        var method = operation switch
        {
            IInvocationOperation call => call.TargetMethod,
            IMethodReferenceOperation reference => reference.Method,
            _ => null,
        };

        if (method is not { MethodKind: MethodKind.LocalFunction }
            || !ComputationLambdas.IsDeclaredOutside(method, computationScope)
            || !visited.Add(method)
            || ComputationLambdas.ResolveMethodBody(method, semanticModel) is not { } helper)
        {
            return;
        }

        foreach (var inner in ComputationLambdas.Descend(helper.Body))
        {
            foreach (var target in MutationTargets(inner))
            {
                ReportIfShared(context, target, helper.Scope, computationScope);
            }

            InspectCalledLocalFunction(context, inner, computationScope, semanticModel, visited);
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

    private static void ReportIfShared(OperationAnalysisContext context, IOperation target, SyntaxNode scope, SyntaxNode computationScope)
    {
        var (kind, symbol) = ResolveSharedRoot(target, scope);
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
    private static (string? kind, ISymbol? symbol) ResolveSharedRoot(IOperation target, SyntaxNode scope)
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
                return ResolveThroughReceiver(field.Instance, "field", field.Field, scope);
            case IPropertyReferenceOperation { Property.IsStatic: true } staticProperty:
                return ("static property", staticProperty.Property);
            case IPropertyReferenceOperation property:
                return ResolveThroughReceiver(property.Instance, "property", property.Property, scope);
            case IEventReferenceOperation { Event.IsStatic: true } staticEvent:
                return ("static event", staticEvent.Event);
            case IEventReferenceOperation @event:
                return ResolveThroughReceiver(@event.Instance, "event", @event.Event, scope);
            default:
                return (null, null);
        }
    }

    // A member on `this` is reported as the member. A member on a value-type receiver that is
    // itself shared storage (a captured struct local/parameter, or a nested value-type
    // field/property/this) reports that RECEIVER -- mutating the member mutates the receiver's
    // storage. A member on any other (reference) receiver is left to MZR001.
    private static (string? kind, ISymbol? symbol) ResolveThroughReceiver(IOperation? receiver, string memberKind, ISymbol member, SyntaxNode scope)
    {
        switch (receiver)
        {
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }:
                return (memberKind, member);
            case ILocalReferenceOperation { Local.Type.IsValueType: true } local when IsDeclaredOutside(local.Local, scope):
                return ("captured local", local.Local);
            case IParameterReferenceOperation { Parameter.Type.IsValueType: true } parameter when IsDeclaredOutside(parameter.Parameter, scope):
                return ("captured parameter", parameter.Parameter);
            case IFieldReferenceOperation { Type.IsValueType: true } outerField:
                return ResolveSharedRoot(outerField, scope);
            case IPropertyReferenceOperation { Type.IsValueType: true } outerProperty:
                return ResolveSharedRoot(outerProperty, scope);
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
