using System.Collections.Concurrent;
using System.Reflection;

namespace MemoizR;

/// <summary>
/// Structural "Sendable" verification (issue #36): decides whether values of a type are safe to
/// hand across concurrently running async flows. MemoizR publishes a node's value reference
/// tear-free (the volatile ValueBox), but it cannot stop two flows from mutating the same object
/// the reference points at -- that is the data-race surface Swift closes with its compile-time
/// Sendable check. C# has no such compiler enforcement, so this is the runtime half of the
/// guarantee, enforced at node creation by <see cref="MemoFactoryOptions.StrictSendableChecks"/>.
///
/// A type is Sendable when every value of it is either deeply immutable or internally
/// synchronized:
///  - primitives, enums, and a small list of known-immutable BCL types (string, decimal, Uri, ...);
///  - immutable/frozen and concurrent collections, when their type arguments are Sendable;
///  - value types whose instance fields are all of Sendable type (the fields themselves may be
///    writable: every read of a struct value yields a private copy, so only references reachable
///    from the copy can alias shared state);
///  - reference types whose instance fields are all readonly (init-only counts) AND of Sendable
///    type, with no non-private settable (non-init) properties, checked across the whole
///    inheritance chain -- the property rule is shared with the MZR001 analyzer, where it is
///    what catches metadata types whose private fields the compiler does not import;
///  - anything marked <see cref="SendableAttribute"/> (trusted, like Swift's @unchecked Sendable).
///
/// Interfaces, abstract classes, object, delegates, and arrays are not Sendable: the first three
/// because the runtime implementation cannot be verified from the static type, delegates because
/// they can capture arbitrary mutable state, arrays because their elements are always writable.
/// Known limitation (documented in ADR 0003): a non-sealed class is judged by its own declared
/// structure -- a mutable subclass smuggled in through an upcast is not caught at creation time.
/// </summary>
public static class SendableChecker
{
    // Verdicts are immutable per closed type, so they are computed once and cached. Only the
    // top-level entry point writes the cache: results computed mid-recursion can rest on the
    // cycle assumption for an outer type whose own verdict is still pending, and caching those
    // could publish a wrong verdict for the inner type.
    private static readonly ConcurrentDictionary<Type, string?> Cache = new();

    // Immutable (or, for CancellationToken/Task, internally synchronized) BCL types that the
    // structural walk would reject because they hide caches or arrays behind their immutable API.
    private static readonly HashSet<Type> KnownSendable =
    [
        typeof(string),
        typeof(decimal),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(TimeSpan),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(Guid),
        typeof(Uri),
        typeof(Version),
        typeof(System.Numerics.BigInteger),
        typeof(CancellationToken),
        typeof(Task),
    ];

    // Generic definitions that are safe to share when their type arguments are: Task<T> is
    // multi-await safe (unlike ValueTask<T>, which is single-consumption and deliberately absent).
    private static readonly HashSet<Type> KnownSendableGenericDefinitions =
    [
        typeof(Task<>),
    ];

    // Collections in these namespaces are immutable or internally synchronized by contract; only
    // their type arguments need verification (the elements are what the consumers share).
    private static readonly HashSet<string> ImmutableOrSynchronizedNamespaces =
    [
        "System.Collections.Immutable",
        "System.Collections.Frozen",
        "System.Collections.Concurrent",
    ];

    public static bool IsSendable(Type type)
    {
        return GetFailureReason(type) is null;
    }

    public static bool IsSendable(Type type, out string? failureReason)
    {
        failureReason = GetFailureReason(type);
        return failureReason is null;
    }

    /// <summary>
    /// Throws when <paramref name="type"/> is not Sendable, with the structural reason and how to
    /// fix it. The runtime analog of a Swift strict-concurrency compile error.
    /// </summary>
    public static void EnsureSendable(Type type)
    {
        var reason = GetFailureReason(type);
        if (reason is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{Pretty(type)} is not Sendable: {reason}. Values of this type are shared by the reactive graph " +
            "across concurrently running flows, so they must be deeply immutable or thread-safe. Use an " +
            "immutable type (a record with init-only members, a readonly struct, an immutable/frozen/concurrent " +
            "collection), or mark the type with [MemoizR.Sendable] to assert that it is thread-safe.");
    }

    private static string? GetFailureReason(Type type)
    {
        return Cache.GetOrAdd(type, static t => Check(t, []));
    }

    // Read-only cache probe for the recursion: never writes (see Cache comment).
    private static string? CheckCached(Type type, HashSet<Type> inProgress)
    {
        return Cache.TryGetValue(type, out var cached) ? cached : Check(type, inProgress);
    }

    private static string? Check(Type type, HashSet<Type> inProgress)
    {
        if (type.IsPrimitive || type.IsEnum || KnownSendable.Contains(type))
        {
            return null;
        }

        if (type.IsGenericType && KnownSendableGenericDefinitions.Contains(type.GetGenericTypeDefinition()))
        {
            return CheckTypeArguments(type, inProgress);
        }

        // The trust escape hatch comes before the structural rejections so that an interface or
        // internally-synchronized class can be opted in.
        if (type.IsDefined(typeof(SendableAttribute), inherit: false))
        {
            return null;
        }

        if (type.Namespace is { } ns && ImmutableOrSynchronizedNamespaces.Contains(ns))
        {
            return CheckTypeArguments(type, inProgress);
        }

        return UnverifiableCategoryReason(type) ?? CheckFields(type, inProgress);
    }

    // Categories that can never be verified structurally, whatever their fields say.
    private static string? UnverifiableCategoryReason(Type type)
    {
        if (type.IsArray)
        {
            return $"{Pretty(type)} is an array, and array elements are always writable shared state " +
                   "(consider ImmutableArray<T> or another System.Collections.Immutable collection)";
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return $"{Pretty(type)} is a delegate, and delegates can capture arbitrary mutable state";
        }

        if (type == typeof(object))
        {
            return "object gives no immutability guarantee";
        }

        if (type.IsInterface || type.IsAbstract)
        {
            return $"{Pretty(type)} is {(type.IsInterface ? "an interface" : "an abstract class")}, so the " +
                   "runtime implementation cannot be verified from the static type";
        }

        if (type.IsPointer || type.IsByRef)
        {
            return $"{Pretty(type)} is a pointer/by-ref type";
        }

        return null;
    }

    private static string? CheckFields(Type type, HashSet<Type> inProgress)
    {
        // A type reachable from its own fields (e.g. a linked record) is assumed Sendable at the
        // point of re-entry: mutability is detected at the field where it occurs, so the cycle
        // itself proves nothing and must not recurse forever.
        if (!inProgress.Add(type))
        {
            return null;
        }

        try
        {
            for (var t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
            {
                var reason = CheckDeclaredMembers(type, t, inProgress);
                if (reason != null)
                {
                    return reason;
                }
            }

            return null;
        }
        finally
        {
            inProgress.Remove(type);
        }
    }

    private static string? CheckDeclaredMembers(Type type, Type declaringLevel, HashSet<Type> inProgress)
    {
        if (!type.IsValueType)
        {
            var settable = CheckProperties(type, declaringLevel);
            if (settable != null)
            {
                return settable;
            }
        }

        foreach (var field in declaringLevel.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            // A reference type shares one instance among all consumers, so every field must be
            // readonly. A value type is copied on every read, so a writable field only mutates
            // the reader's private copy -- but the field's TYPE must still be Sendable, because a
            // copied reference aliases the same object.
            if (!type.IsValueType && !field.IsInitOnly)
            {
                return $"{Pretty(type)} has writable instance {FieldDisplay(field)}";
            }

            var inner = CheckCached(field.FieldType, inProgress);
            if (inner != null)
            {
                return $"{Pretty(type)}'s {FieldDisplay(field)} is of non-Sendable type {Pretty(field.FieldType)} ({inner})";
            }
        }

        return null;
    }

    // A settable (non-init) property that is not private is a mutation surface on the shared
    // instance, whatever the backing storage looks like. Kept in lockstep with the MZR001
    // analyzer's rule, where it carries extra weight: the compiler does not import private
    // metadata fields, so List<int> is caught there by its settable Capacity/indexer rather
    // than by its invisible '_items'. Value types are exempt for the same reason as writable
    // fields: a setter mutates the reader's private copy.
    private static string? CheckProperties(Type type, Type declaringLevel)
    {
        foreach (var property in declaringLevel.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var setter = property.SetMethod;
            if (setter is null || setter.IsPrivate || IsInitOnly(setter))
            {
                continue;
            }

            return property.GetIndexParameters().Length > 0
                ? $"{Pretty(type)} has a settable indexer"
                : $"{Pretty(type)} has settable property '{property.Name}' (use init or get-only)";
        }

        return null;
    }

    // init accessors are ordinary setters carrying a modreq of IsExternalInit on their return.
    private static bool IsInitOnly(MethodInfo setter)
    {
        foreach (var modifier in setter.ReturnParameter.GetRequiredCustomModifiers())
        {
            if (modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit")
            {
                return true;
            }
        }

        return false;
    }

    private static string? CheckTypeArguments(Type type, HashSet<Type> inProgress)
    {
        if (!type.IsGenericType)
        {
            return null;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            var inner = CheckCached(argument, inProgress);
            if (inner != null)
            {
                return $"{Pretty(type)} carries non-Sendable type argument {Pretty(argument)} ({inner})";
            }
        }

        return null;
    }

    // Failure reasons must point the user at THEIR member, so compiler-generated backing fields
    // are reported as the auto-property they belong to.
    private static string FieldDisplay(FieldInfo field)
    {
        var name = field.Name;
        if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
        {
            return $"auto-property '{name[1..name.IndexOf('>')]}' (declared with a set accessor; use init or get-only)";
        }

        return $"field '{name}'";
    }

    private static string Pretty(Type type)
    {
        if (type.IsArray)
        {
            return Pretty(type.GetElementType()!) + "[]";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Pretty(underlying) + "?";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var backtick = name.IndexOf('`');
        if (backtick >= 0)
        {
            name = name[..backtick];
        }

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Pretty))}>";
    }
}
