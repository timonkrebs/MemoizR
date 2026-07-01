using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers;

// Compile-time mirror of the runtime SendableChecker (MemoizR/SendableChecker.cs): the same
// classification, expressed over ITypeSymbols. The two walks must be kept in lockstep -- a type
// the analyzer accepts but strict mode throws on (or vice versa) erodes trust in both (ADR 0004
// records this as a maintenance contract).
//
// One deliberate divergence: an unbound type parameter is accepted. There is no `Sendable`
// constraint to require on it, so flagging every generic passthrough would force suppressions
// instead of fixes; the closed instantiation is checked at its own creation site.
internal sealed class SendableSymbolClassifier
{
    // One classifier (and so one cache) per compilation: symbols must not outlive it. Only the
    // top-level entry caches -- a verdict computed mid-recursion can rest on the cycle assumption
    // for an outer type whose own verdict is still pending.
    private readonly ConcurrentDictionary<ITypeSymbol, string?> cache = new(SymbolEqualityComparer.Default);

    public string? GetNotSendableReason(ITypeSymbol type)
    {
        return cache.GetOrAdd(type, t => Check(t, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)));
    }

    private string? CheckCached(ITypeSymbol type, HashSet<ITypeSymbol> inProgress)
    {
        return cache.TryGetValue(type, out var cached) ? cached : Check(type, inProgress);
    }

    private string? Check(ITypeSymbol type, HashSet<ITypeSymbol> inProgress)
    {
        if (IsAlwaysSendable(type) || type is ITypeParameterSymbol)
        {
            return null;
        }

        if (type is IArrayTypeSymbol)
        {
            return $"{Display(type)} is an array, and array elements are always writable shared state " +
                   "(consider ImmutableArray<T> or another System.Collections.Immutable collection)";
        }

        if (type is not INamedTypeSymbol named)
        {
            // Pointers, function pointers, dynamic: nothing to verify.
            return $"{Display(type)} cannot be verified to be immutable or thread-safe";
        }

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return CheckCached(named.TypeArguments[0], inProgress);
        }

        if (IsTaskOfT(named) || IsKnownSendableCollection(named))
        {
            return CheckTypeArguments(named, inProgress);
        }

        if (HasSendableAttribute(named))
        {
            return null;
        }

        // Reject unverifiable categories (interface, abstract class, delegate, object). Runs
        // after the known-definitions green-list above (FrozenDictionary/FrozenSet are
        // deliberately abstract) but before the structural walk, kept in lockstep with the
        // runtime checker: an interface or abstract base (e.g. IProducerConsumerCollection<T>)
        // reveals nothing about the concrete runtime type, whatever namespace it lives in.
        var categoryReason = UnverifiableCategoryReason(named);
        if (categoryReason != null)
        {
            return categoryReason;
        }

        return CheckFields(named, inProgress);
    }

    private static bool IsAlwaysSendable(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
            case SpecialType.System_String:
            case SpecialType.System_Decimal:
            case SpecialType.System_DateTime:
                return true;
        }

        return IsKnownImmutable(type);
    }

    // The green-list of the runtime checker: immutable (or, for CancellationToken/Task,
    // internally synchronized) BCL types whose structure hides caches/arrays behind an
    // immutable API. Framework-assembly gated like the collection list: a source-declared
    // lookalike (`namespace System { class Uri { public int State; } }`) binds over the BCL
    // type and must go through the structural walk, as the runtime's typeof identity would
    // reject it.
    private static bool IsKnownImmutable(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.Arity != 0 || !IsDeclaredInFrameworkAssembly(named))
        {
            return false;
        }

        var ns = named.ContainingNamespace?.ToDisplayString();
        switch (named.Name)
        {
            case "Guid":
            case "TimeSpan":
            case "DateTimeOffset":
            case "DateOnly":
            case "TimeOnly":
            case "Uri":
            case "Version":
            // Type is runtime-managed and effectively immutable; it is also what every non-sealed
            // record's synthesized `protected virtual Type EqualityContract` property returns, so
            // rejecting it (System.Type is abstract) would falsely reject every non-sealed record.
            case "Type":
                return ns == "System";
            case "BigInteger":
                return ns == "System.Numerics";
            case "CancellationToken":
                return ns == "System.Threading";
            case "Task":
                return ns == "System.Threading.Tasks";
            default:
                return false;
        }
    }

    // Task<T> is multi-await safe; ValueTask<T> is single-consumption and deliberately absent.
    private static bool IsTaskOfT(INamedTypeSymbol named)
    {
        var definition = named.OriginalDefinition;
        return definition.Name == "Task"
            && definition.Arity == 1
            && definition.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
            && IsDeclaredInFrameworkAssembly(definition);
    }

    // The known framework collections that are immutable or internally synchronized BY
    // CONTRACT, matched by definition (namespace + name + arity, top-level only, declared in a
    // framework assembly) -- NOT by namespace or name alone: any project can declare its own
    // types (even exact-name lookalikes) inside a System.Collections.* namespace, and those
    // must fall through to the structural checks, as the runtime checker's typeof-identity
    // match would reject them. Kept in lockstep with that runtime list. Nested helpers like
    // ImmutableList<T>.Builder are distinct definitions and fall through too (their settable
    // indexers reject them). FrozenDictionary/FrozenSet are abstract by design, so this trust
    // is granted before the abstract-category rejection.
    private static bool IsKnownSendableCollection(INamedTypeSymbol named)
    {
        var definition = named.OriginalDefinition;
        if (definition.ContainingType is not null || !IsDeclaredInFrameworkAssembly(definition))
        {
            return false;
        }

        switch (definition.ContainingNamespace?.ToDisplayString())
        {
            case "System.Collections.Immutable":
                return definition.Name switch
                {
                    "ImmutableArray" or "ImmutableHashSet" or "ImmutableList"
                        or "ImmutableQueue" or "ImmutableSortedSet" or "ImmutableStack" => definition.Arity == 1,
                    "ImmutableDictionary" or "ImmutableSortedDictionary" => definition.Arity == 2,
                    _ => false,
                };
            case "System.Collections.Frozen":
                return definition.Name switch
                {
                    "FrozenSet" => definition.Arity == 1,
                    "FrozenDictionary" => definition.Arity == 2,
                    _ => false,
                };
            case "System.Collections.Concurrent":
                return definition.Name switch
                {
                    "ConcurrentQueue" or "ConcurrentStack" or "ConcurrentBag" or "BlockingCollection" => definition.Arity == 1,
                    "ConcurrentDictionary" => definition.Arity == 2,
                    _ => false,
                };
            default:
                return false;
        }
    }

    // A symbol only counts as THE framework type when it comes from a framework assembly: a
    // source declaration is by definition the user's (source wins over metadata on a name
    // clash, so the candidate here IS what the code binds to), and a metadata lookalike from an
    // ordinary library must not be blessed either. The assembly-name set covers where the
    // green-listed types (or their facades) live across TFMs -- .NET (split System.* runtime
    // assemblies, System.Private.CoreLib in runtime-assembly compilations), .NET Framework
    // (mscorlib/System/System.Numerics), and the netstandard/System.Runtime facades.
    private static bool IsDeclaredInFrameworkAssembly(INamedTypeSymbol definition)
    {
        if (definition.Locations.Any(location => location.IsInSource))
        {
            return false;
        }

        return definition.ContainingAssembly?.Identity.Name is
            "System.Collections.Immutable" or "System.Collections.Concurrent" or "System.Collections"
            or "System.Runtime" or "System.Private.CoreLib" or "mscorlib" or "netstandard" or "System"
            or "System.Private.Uri" or "System.Runtime.Numerics" or "System.Numerics";
    }

    private static bool HasSendableAttribute(INamedTypeSymbol named)
    {
        foreach (var attribute in named.OriginalDefinition.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is { Name: "SendableAttribute" }
                && attributeClass.ContainingNamespace?.ToDisplayString() == "MemoizR")
            {
                return true;
            }
        }

        return false;
    }

    // Categories that can never be verified structurally, whatever their fields say.
    private static string? UnverifiableCategoryReason(INamedTypeSymbol named)
    {
        if (named.SpecialType == SpecialType.System_Object)
        {
            return "object gives no immutability guarantee";
        }

        if (named.TypeKind == TypeKind.Delegate)
        {
            return $"{Display(named)} is a delegate, and delegates can capture arbitrary mutable state";
        }

        if (named.TypeKind == TypeKind.Interface || named.IsAbstract)
        {
            return $"{Display(named)} is {(named.TypeKind == TypeKind.Interface ? "an interface" : "an abstract class")}, " +
                   "so the runtime implementation cannot be verified from the static type";
        }

        return null;
    }

    private string? CheckFields(INamedTypeSymbol named, HashSet<ITypeSymbol> inProgress)
    {
        // Self-referential types (linked records) terminate via the cycle assumption: mutability
        // is detected at the field where it occurs, so re-entering a type proves nothing.
        if (!inProgress.Add(named))
        {
            return null;
        }

        try
        {
            for (var current = named; current != null && !IsRootType(current); current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    var reason = CheckMember(named, member, inProgress);
                    if (reason != null)
                    {
                        return reason;
                    }
                }
            }

            return null;
        }
        finally
        {
            inProgress.Remove(named);
        }
    }

    // A settable (non-init) property that is not private is a mutation surface, on any consumer's
    // thread. This rule carries the weight for METADATA types: under the compiler's default
    // MetadataImportOptions.Public their private fields are not even imported, so List<int> is
    // caught by its settable Capacity/indexer rather than by its invisible '_items'. Value types
    // are exempt for the same reason as fields: a setter mutates the reader's private copy. The
    // property TYPE is also checked (not just the setter), so a get-only `public List<int> Items
    // { get; }` on a metadata class -- whose private backing field the field walk cannot see -- is
    // rejected the way the runtime checker rejects it through that field.
    private string? CheckPropertyMember(INamedTypeSymbol named, IPropertySymbol property, HashSet<ITypeSymbol> inProgress)
    {
        if (!named.IsValueType && !property.IsStatic && HasVisibleNonInitSetter(property))
        {
            return $"{Display(named)} has {SettableDisplay(property)}";
        }

        if (property.IsStatic || property.IsIndexer || property.GetMethod is null
            || property.DeclaredAccessibility == Accessibility.Private)
        {
            return null;
        }

        var propertyReason = CheckCached(property.Type, inProgress);
        return propertyReason is null
            ? null
            : $"{Display(named)}'s property '{property.Name}' is of non-Sendable type {Display(property.Type)} ({propertyReason})";
    }

    private string? CheckMember(INamedTypeSymbol named, ISymbol member, HashSet<ITypeSymbol> inProgress)
    {
        if (member is IPropertySymbol property)
        {
            return CheckPropertyMember(named, property, inProgress);
        }

        // A visible (non-private) instance event is a mutation surface like a settable property:
        // subscribing/unsubscribing mutates the shared instance's delegate. The runtime checker
        // catches an auto-event via its (writable, delegate-typed) backing field, but under the
        // compiler's default MetadataImportOptions.Public that field is not imported, so the
        // analyzer must reject the event itself to stay in lockstep. (Value types: exempt, like
        // fields/properties -- the event lives on the reader's private copy.)
        if (member is IEventSymbol { IsStatic: false } @event
            && !named.IsValueType
            && @event.DeclaredAccessibility != Accessibility.Private)
        {
            return $"{Display(named)} has event '{@event.Name}' (subscribing mutates the shared instance)";
        }

        if (member is not IFieldSymbol field || field.IsStatic)
        {
            return null;
        }

        // A reference type shares one instance among all consumers, so every field must be
        // readonly. A value type is copied on every read, so a writable field only mutates the
        // reader's private copy -- but the field's TYPE must still be Sendable, because a copied
        // reference aliases the same object.
        if (!named.IsValueType && !field.IsReadOnly)
        {
            return $"{Display(named)} has writable instance {MemberDisplay(field)}";
        }

        var inner = CheckCached(field.Type, inProgress);
        if (inner != null)
        {
            return $"{Display(named)}'s {MemberDisplay(field)} is of non-Sendable type {Display(field.Type)} ({inner})";
        }

        return null;
    }

    private static bool HasVisibleNonInitSetter(IPropertySymbol property)
    {
        // Private setters are covered by the field walk where visible (source types) and are
        // unreachable by consumers; everything else can mutate the shared instance.
        return property.SetMethod is { IsInitOnly: false } setter
            && setter.DeclaredAccessibility != Accessibility.Private;
    }

    private static string SettableDisplay(IPropertySymbol property)
    {
        return property.IsIndexer
            ? "a settable indexer"
            : $"settable property '{property.Name}' (use init or get-only)";
    }

    private static bool IsRootType(INamedTypeSymbol type)
    {
        return type.SpecialType == SpecialType.System_Object || type.SpecialType == SpecialType.System_ValueType;
    }

    private string? CheckTypeArguments(INamedTypeSymbol named, HashSet<ITypeSymbol> inProgress)
    {
        foreach (var argument in named.TypeArguments)
        {
            var inner = CheckCached(argument, inProgress);
            if (inner != null)
            {
                return $"{Display(named)} carries non-Sendable type argument {Display(argument)} ({inner})";
            }
        }

        return null;
    }

    // Failure reasons must point at the member the user wrote, not a compiler-generated backing
    // field; symbols make this direct via AssociatedSymbol.
    private static string MemberDisplay(IFieldSymbol field)
    {
        return field.AssociatedSymbol is IPropertySymbol property
            ? $"auto-property '{property.Name}' (declared with a set accessor; use init or get-only)"
            : $"field '{field.Name}'";
    }

    internal static string Display(ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }
}
