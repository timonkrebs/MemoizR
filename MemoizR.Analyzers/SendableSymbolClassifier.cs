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

    // Whether the type is on any framework green-list (known immutables, Task<T>, the known
    // collections): used by MZR006 to avoid nagging about non-sealed BCL types like Uri, whose
    // accidental subclassing is not a plausible failure mode.
    internal static bool IsFrameworkGreenListed(INamedTypeSymbol named)
    {
        return IsKnownImmutable(named) || IsTaskOfT(named) || IsKnownSendableCollection(named);
    }

    // A green-listed non-generic class whose green-listed GENERIC sibling derives from it
    // (Task <- Task<TResult>): the upcast needs no user subclass -- every async method
    // manufactures one -- so an arbitrary payload can ride behind the surface past checks
    // that trust the declared type wholesale.
    internal static bool HasAGreenListedGenericSubclass(INamedTypeSymbol named)
    {
        if (named.Arity != 0 || named.IsValueType || named.ContainingNamespace is null)
        {
            return false;
        }

        return named.ContainingNamespace.GetTypeMembers(named.Name)
            .Any(sibling => sibling.Arity > 0
                && IsFrameworkGreenListed(sibling)
                && DerivesFrom(sibling, named));
    }

    private static bool DerivesFrom(INamedTypeSymbol candidate, INamedTypeSymbol baseType)
    {
        for (var current = candidate.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    // The type-parameter exemption above (Check) is deliberate for creation sites, whose
    // closed instantiation is checked later; rules WITHOUT such a later site (MZR004's
    // statics, MZR005's generic transfer helpers) track a type parameter unless its
    // constraints prove it harmless. Only `where T : Enum` does: an enum is immutable for
    // every instantiation. `unmanaged` is NOT enough -- an `unsafe struct` with a pointer
    // field satisfies it, and a copied pointer still aliases writable memory (the runtime
    // rejects pointer fields, so the closed type fails there too); a plain `struct`
    // constraint promises even less.
    internal static bool IsProvenSendableByConstraints(ITypeParameterSymbol parameter)
    {
        return parameter.ConstraintTypes.Any(constraint => constraint.SpecialType == SpecialType.System_Enum);
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
    internal static bool IsDeclaredInFrameworkAssembly(INamedTypeSymbol definition)
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

    // Internal because MZR004's type-parameter walk uses [Sendable] trust as its descent
    // boundary: an attributed type's thread-safety assertion does not rest on its type
    // arguments (MemoizR's own nodes are internally synchronized for any T).
    internal static bool HasSendableAttribute(INamedTypeSymbol named)
    {
        foreach (var attribute in named.OriginalDefinition.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is { Name: "SendableAttribute" }
                && attributeClass.ContainingNamespace?.ToDisplayString() == "MemoizR"
                && IsTheMemoizRAttribute(attributeClass))
            {
                return true;
            }
        }

        return false;
    }

    // The trust escape hatch must be THE library's attribute, not a source-shadowed lookalike
    // (which binds over the referenced one with a conflict warning): strict runtime mode checks
    // typeof(MemoizR.SendableAttribute) identity and would reject the same type, so trusting a
    // fake here would leave the analyzer silent for a value that throws at node creation.
    private static bool IsTheMemoizRAttribute(INamedTypeSymbol attributeClass)
    {
        return !attributeClass.Locations.Any(location => location.IsInSource)
            && attributeClass.ContainingAssembly?.Identity.Name == "MemoizR";
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

        // Polymorphic recursion, which the re-entry check above can never catch, is cut by
        // the shared divergence bound and lands on the cycle assumption.
        if (named.IsGenericType && IsDivergentReinstantiation(named, inProgress))
        {
            inProgress.Remove(named);
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

        // Private properties are unreachable by consumers -- EXCEPT explicit interface
        // implementations, which are declared private but reachable through a cast to the
        // interface (kept in lockstep with the runtime checker's IsVisibleAccessor). Indexers
        // are checked like any property: a computed get-only `List<int> this[int i]` hands out
        // the same shared mutable state.
        if (property.IsStatic || property.GetMethod is null
            || (property.DeclaredAccessibility == Accessibility.Private
                && (property.ExplicitInterfaceImplementations.IsEmpty || named.IsValueType)))
        {
            return null;
        }

        var propertyReason = CheckCached(property.Type, inProgress);
        if (propertyReason is null)
        {
            return null;
        }

        var display = property.IsIndexer ? "indexer" : $"property '{property.Name}'";
        return $"{Display(named)}'s {display} is of non-Sendable type {Display(property.Type)} ({propertyReason})";
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
            && (@event.DeclaredAccessibility != Accessibility.Private
                || !@event.ExplicitInterfaceImplementations.IsEmpty))
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
        // unreachable by consumers -- except explicit interface implementations, reachable
        // through a cast to the interface; everything else can mutate the shared instance.
        return property.SetMethod is { IsInitOnly: false } setter
            && (setter.DeclaredAccessibility != Accessibility.Private
                || !setter.ExplicitInterfaceImplementations.IsEmpty);
    }

    private static string SettableDisplay(IPropertySymbol property)
    {
        return property.IsIndexer
            ? "a settable indexer"
            : $"settable property '{property.Name}' (use init or get-only)";
    }

    internal static bool IsRootType(INamedTypeSymbol type)
    {
        return type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;
    }

    // POLYMORPHIC recursion constructs a FRESH closed symbol per level (Box<T> exposing a
    // Box<List<T>> member), which a visited set can never catch. Its signature is a
    // NON-SHRINKING re-occurrence of the same definition on the recursion PATH: finite
    // hand-written nesting (Box<Box<Box<List<int>>>>) strictly shrinks at every recursive
    // step and is walked to the bottom however deep it goes. A divergent expansion is walked
    // through its FIRST re-instantiation -- substituted members can flip the verdict exactly
    // one level down (Box<T> { Box<List<T>> Next; T Value; } hides the List in the second
    // level's Value) -- and cut at the second. Counted on the path, not globally: sibling
    // instantiations of one definition are not recursion and must all be walked. Shared by
    // the classifier's field walk and the MZR004/MZR006 type-graph walks; kept in lockstep
    // with the runtime checker.
    internal static bool IsDivergentReinstantiation(INamedTypeSymbol named, IEnumerable<ITypeSymbol> priorOnPath)
    {
        return priorOnPath.Count(prior => prior is INamedTypeSymbol other
            && !SymbolEqualityComparer.Default.Equals(other, named)
            && SymbolEqualityComparer.Default.Equals(other.OriginalDefinition, named.OriginalDefinition)
            && TypeSize(other) <= TypeSize(named)) >= 2;
    }

    // The number of type nodes in the constructed reference: Box<List<int>> is 3. Finite
    // nesting shrinks this at every recursive step; polymorphic recursion grows it. CONTAINING
    // type arguments count too: a nested Outer<T>.Holder substitutes through them (the runtime
    // Type flattens outer arguments into GetGenericArguments, so this stays in lockstep).
    internal static int TypeSize(ITypeSymbol type)
    {
        return type switch
        {
            IArrayTypeSymbol array => 1 + TypeSize(array.ElementType),
            INamedTypeSymbol named => 1 + named.TypeArguments.Sum(TypeSize)
                + (named.ContainingType is { } containing ? TypeSize(containing) - 1 : 0),
            _ => 1,
        };
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

    // Whether the property owns a backing slot (an auto-property): the compiler ties the
    // synthesized field to the property via AssociatedSymbol. Computed getters store nothing
    // -- state they hand out lives in some field/auto-property flagged at ITS declaration --
    // so the static-state and smuggle walks only count stored members. (Metadata backing
    // fields are not imported under MetadataImportOptions.Public, so metadata auto-properties
    // read as computed: an accepted best-effort miss.)
    internal static bool HasBackingSlot(IPropertySymbol property)
    {
        return property.ContainingType.GetMembers().OfType<IFieldSymbol>()
            .Any(field => SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property));
    }

    // The member types a SOURCE-DECLARED type stores, base chain included: explicit fields and
    // auto-properties (a computed member holds no slot), inherited ones storing state exactly
    // like declared ones. Metadata members are import-limited, and framework internals are not
    // the user's surface (green-listed containers expose their payload via type arguments), so
    // metadata types contribute nothing.
    internal static IEnumerable<ITypeSymbol> StoredInstanceMemberTypesOf(INamedTypeSymbol named)
    {
        if (!named.Locations.Any(location => location.IsInSource))
        {
            yield break;
        }

        for (var current = named; current is not null && !IsRootType(current); current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                var memberType = member switch
                {
                    IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } field => field.Type,
                    IPropertySymbol { IsStatic: false } property when HasBackingSlot(property) => property.Type,
                    _ => null,
                };

                if (memberType is not null)
                {
                    yield return memberType;
                }
            }
        }
    }

    internal static string Display(ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }
}
