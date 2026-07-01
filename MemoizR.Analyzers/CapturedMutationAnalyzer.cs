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
            foreach (var operation in ComputationLambdas.Descend(computation.Body))
            {
                foreach (var target in MutationTargets(operation))
                {
                    ReportIfShared(context, target, computation.Scope);
                }
            }
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
    // overwhelmingly pure), and property receivers (the getter hands out a copy, so the call
    // mutates a temporary -- a lost write, not shared mutation; direct writes through a
    // value-type property are already compiler errors).
    private static bool IsMutatingValueReceiverCall(IInvocationOperation call)
    {
        if (call.Instance is not { Type.IsValueType: true } || call.Instance is IPropertyReferenceOperation)
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

    private static void ReportIfShared(OperationAnalysisContext context, IOperation target, SyntaxNode scope)
    {
        var (kind, symbol) = ResolveSharedRoot(target, scope);
        if (kind is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.CapturedMutation,
            target.Syntax.GetLocation(),
            kind,
            symbol!.Name));
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

    // "Captured" = declared outside the computation's declaring syntax (the lambda expression,
    // or the method/local-function declaration for a method-group computation). The span test
    // (rather than comparing containing symbols) is what keeps nested non-computation lambdas
    // correct: a local declared in a nested LINQ lambda belongs to the computation and must not
    // be flagged, while a local of the enclosing method is shared and must be.
    private static bool IsDeclaredOutside(ISymbol symbol, SyntaxNode scope)
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
}
