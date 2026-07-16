using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace MemoizR.Tests;

// Contracts of the Sendable analog (issue #36): SendableChecker's structural verdicts on what
// may safely cross concurrently running flows, the [Sendable] trust escape hatch (the
// @unchecked-Sendable analog), and MemoFactoryOptions.StrictSendableChecks enforcing the check
// at node creation for every node type whose value the graph shares across flows.
public class SendableCheckerTests
{
    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(double))]
    [InlineData(typeof(string))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(DayOfWeek))] // enums carry no mutable state
    [InlineData(typeof(int?))] // Nullable<T> of a Sendable T
    [InlineData(typeof((int, string)))] // tuple fields are writable, but every read is a private copy
    [InlineData(typeof(KeyValuePair<string, int>))]
    [InlineData(typeof(Task))]
    [InlineData(typeof(Task<int>))] // multi-await safe carrier of a Sendable result
    [InlineData(typeof(Type))] // runtime-managed, effectively immutable (and every record's EqualityContract)
    public void PrimitivesImmutablesAndValueCompositions_AreSendable(Type type)
    {
        Assert.True(SendableChecker.IsSendable(type, out var reason), reason);
    }

    [Theory]
    [InlineData(typeof(List<int>))] // mutable collection
    [InlineData(typeof(int[]))] // array elements are always writable
    [InlineData(typeof(Dictionary<string, int>))]
    [InlineData(typeof(object))] // no guarantee at all
    [InlineData(typeof(IEnumerable<int>))] // interface: implementation unverifiable
    [InlineData(typeof(Func<int>))] // delegate: may capture anything
    [InlineData(typeof(Task<List<int>>))] // safe carrier, unsafe payload
    [InlineData(typeof(ImmutableList<int>.Builder))] // nested mutable helper in an "immutable" namespace
    [InlineData(typeof(ImmutableArray<int>.Builder))]
    [InlineData(typeof(IProducerConsumerCollection<int>))] // interface in a trusted namespace: impl unverifiable
    public void MutableOrUnverifiableTypes_AreNotSendable(Type type)
    {
        Assert.False(SendableChecker.IsSendable(type, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(typeof(ImmutableArray<int>))]
    [InlineData(typeof(ImmutableList<string>))]
    [InlineData(typeof(ImmutableDictionary<string, decimal>))]
    [InlineData(typeof(ConcurrentDictionary<string, int>))] // internally synchronized
    [InlineData(typeof(ConcurrentQueue<int>))]
    [InlineData(typeof(System.Collections.Frozen.FrozenDictionary<string, int>))] // abstract by design: trusted by definition, before the category rejection
    [InlineData(typeof(System.Collections.Frozen.FrozenSet<int>))]
    public void ImmutableAndConcurrentCollections_AreSendable_WhenTheirElementsAre(Type type)
    {
        Assert.True(SendableChecker.IsSendable(type, out var reason), reason);
    }

    [Fact]
    public void UserTypeDeclaredInAFrameworkCollectionNamespace_IsNotBlessedByTheNamespace()
    {
        // The collection green-list matches known framework DEFINITIONS by type identity; a
        // project's own type inside System.Collections.Concurrent goes through the structural
        // walk like any other type -- and this one fails it.
        Assert.False(SendableChecker.IsSendable(typeof(HomegrownConcurrentCache), out var reason));
        Assert.Contains("Hits", reason);
    }

    [Fact]
    public void ImmutableCollection_OfMutableElements_IsNotSendable()
    {
        // The collection wrapper is safe; the List<int> elements it hands out are still shared
        // mutable state, so the type-argument check must reject it.
        Assert.False(SendableChecker.IsSendable(typeof(ImmutableList<List<int>>), out var reason));
        Assert.Contains("List", reason);
    }

    [Fact]
    public void Records_WithInitOnlyMembers_AreSendable_IncludingSelfReferential()
    {
        Assert.True(SendableChecker.IsSendable(typeof(SendablePerson), out var personReason), personReason);
        // A linked record reaches its own type through a field; the cycle must terminate and the
        // verdict must come from the fields, which are all readonly here.
        Assert.True(SendableChecker.IsSendable(typeof(SendableLinkedNode), out var nodeReason), nodeReason);
        Assert.True(SendableChecker.IsSendable(typeof(SendablePoint), out var pointReason), pointReason);
    }

    [Fact]
    public void PlainClass_WithInitOnlyAutoProperties_IsSendable()
    {
        // Roslyn emits INITONLY backing fields for { get; init; } auto-properties (init
        // accessors are the sanctioned way to assign readonly fields), so the field walk
        // accepts idiomatic init DTOs -- records and plain classes alike. Pinned because it is
        // easy to assume the opposite.
        Assert.True(SendableChecker.IsSendable(typeof(PlainInitDto), out var reason), reason);
    }

    [Fact]
    public void NonSealedRecord_WithInitOnlyMembers_IsSendable()
    {
        // A non-sealed record synthesizes `protected virtual Type EqualityContract { get; }`; the
        // get-only property-type check must not trip over it -- System.Type is abstract but
        // green-listed as known-immutable. Guards the pairing of the two rules.
        Assert.True(SendableChecker.IsSendable(typeof(SendableOpenRecord), out var reason), reason);
    }

    [Fact]
    public void ComputedGetOnlyProperty_OfMutableType_IsNotSendable()
    {
        // No instance backing field for the field walk to see: the computed property hands out
        // shared mutable state, so the property-TYPE check (in lockstep with MZR001) rejects it.
        Assert.False(SendableChecker.IsSendable(typeof(LeakyComputedView), out var reason));
        Assert.Contains("'Snapshot'", reason);
        Assert.Contains("List", reason);
    }

    [Fact]
    public void Record_WithSettableProperty_IsNotSendable_AndTheReasonNamesTheProperty()
    {
        Assert.False(SendableChecker.IsSendable(typeof(MutableRecord), out var reason));
        // The reason must point at the user's member, not the compiler-generated backing field.
        Assert.Contains("'Name'", reason);
    }

    [Fact]
    public void Class_WithReadonlyFieldOfMutableType_IsNotSendable_AndTheReasonChainsIntoTheField()
    {
        // readonly protects the reference, not the List behind it.
        Assert.False(SendableChecker.IsSendable(typeof(HolderOfMutable), out var reason));
        Assert.Contains("Items", reason);
        Assert.Contains("List", reason);
    }

    [Fact]
    public void SendableAttribute_IsTrusted_WithoutStructuralChecks()
    {
        Assert.True(SendableChecker.IsSendable(typeof(TrustedMutable), out var reason), reason);
    }

    [Fact]
    public void ExplicitInterfaceProperty_OfMutableType_IsNotSendable()
    {
        // Reflection reports explicit implementations as private, but any consumer reaches
        // them by casting to the interface -- the exposed List is shared mutable state.
        Assert.False(SendableChecker.IsSendable(typeof(ExplicitInterfaceLeak), out var reason));
        Assert.Contains("Items", reason);
    }

    [Fact]
    public void ExplicitInterfaceEvent_IsNotSendable()
    {
        // The add accessor reflects as private but is reachable through the interface cast.
        Assert.False(SendableChecker.IsSendable(typeof(ExplicitEventLeak), out var reason));
        Assert.Contains("event", reason);
    }

    [Fact]
    public void GetOnlyIndexer_OfMutableType_IsNotSendable()
    {
        // A computed get-only indexer hands out shared mutable state like a get-only property;
        // no setter, no instance field -- the indexer's return type is the whole leak surface.
        Assert.False(SendableChecker.IsSendable(typeof(IndexerLeak), out var reason));
        Assert.Contains("indexer", reason);
        Assert.Contains("List", reason);
    }

    [Fact]
    public void TypeWithVisibleCustomEvent_IsNotSendable()
    {
        // A custom event (explicit add/remove) has no instance backing field for the field walk,
        // so it must be rejected via the event check; subscribing mutates shared delegate state.
        Assert.False(SendableChecker.IsSendable(typeof(HasCustomEvent), out var reason));
        Assert.Contains("event", reason);
    }

    [Fact]
    public void EnsureSendable_Throws_WithReasonAndFixGuidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SendableChecker.EnsureSendable(typeof(List<int>)));
        Assert.Contains("not Sendable", ex.Message);
        Assert.Contains("[MemoizR.Sendable]", ex.Message);
    }
}

public class ValidateWrittenValuesTests
{
    [Fact]
    public async Task SmuggledMutableSubclass_IsRejectedAtTheWrite()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks | MemoFactoryOptions.ValidateWrittenValues);
        var signal = f.CreateSignal<OpenBase>(new OpenBase()); // declared type passes the creation check

        // The runtime type of the written instance is what the declared-type check cannot see.
        await Assert.ThrowsAsync<InvalidOperationException>(() => signal.Set(new MutableChild()));

        await signal.Set(new OpenBase()); // a well-behaved instance still writes fine
    }

    [Fact]
    public async Task WithoutTheOption_RuntimeTypesAreNotChecked()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks);
        var signal = f.CreateSignal<OpenBase>(new OpenBase());
        await signal.Set(new MutableChild()); // documented hole when the option is off
        Assert.NotNull(signal);
    }

    [Fact]
    public async Task ActorSignal_ValidatesWrites_Too()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.ValidateWrittenValues);
        var signal = f.CreateActorSignal<OpenBase>(new OpenBase());
        await Assert.ThrowsAsync<InvalidOperationException>(() => signal.Set(new MutableChild()));
    }

    [Fact]
    public async Task DisableSendableChecks_AlsoDisablesWriteValidation()
    {
        // The migration escape hatch is COMPLETE: opting out of the Sendable checks must not
        // leave write-time validation armed, or migrating code would still throw on Set.
        var f = new MemoFactory(options: MemoFactoryOptions.ValidateWrittenValues | MemoFactoryOptions.DisableSendableChecks);
        var signal = f.CreateSignal<OpenBase>(new OpenBase());
        await signal.Set(new MutableChild());
        Assert.NotNull(signal);
    }

    [Fact]
    public async Task EagerRelativeSignal_ValidatesTheComputedResult()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.ValidateWrittenValues);
        var relative = f.CreateEagerRelativeSignal<OpenBase>(new OpenBase());
        await Assert.ThrowsAsync<InvalidOperationException>(() => relative.Set(_ => new MutableChild()));
    }

    [Fact]
    public async Task FrameworkPolymorphicValues_KeepTheGreenListVerdict()
    {
        // typeof(int) is a System.RuntimeType: an internal runtime implementation of the
        // green-listed Type. The write check sees the INSTANCE's type, so the entry's trust
        // must extend to the runtime's own subclasses or every Signal<Type> write throws.
        var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks | MemoFactoryOptions.ValidateWrittenValues);
        var signal = f.CreateSignal<Type>(typeof(int));
        await signal.Set(typeof(string));
        Assert.Equal(typeof(string), await signal.Get());
    }

    [Fact]
    public void FrameworkImplementations_OfGreenListedTypes_AreSendable()
    {
        // The green-list trust covers same-assembly framework implementations only: a USER
        // subclass of a green-listed type still walks structurally -- that is the smuggle
        // surface ValidateWrittenValues exists to catch.
        Assert.True(SendableChecker.IsSendable(typeof(int).GetType(), out var reason), reason);
        Assert.False(SendableChecker.IsSendable(typeof(MutableChild)));
    }
}

public class SendingTransferTests
{
    [Fact]
    public void Sending_IsSendable_EvenWhenThePayloadIsNot()
    {
        // The whole point of the wrapper: transfer semantics stand in for immutability.
        Assert.True(SendableChecker.IsSendable(typeof(Sending<List<int>>), out var reason), reason);
    }

    [Fact]
    public void StrictFactory_AcceptsSendingOfNonSendablePayload()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks);
        var signal = f.CreateSignal(Sending.Transfer(new List<int> { 1, 2 }));
        Assert.NotNull(signal);
    }

    [Fact]
    public async Task Receive_HandsOverTheValue_ExactlyOnce()
    {
        var list = new List<int> { 1, 2, 3 };
        var sending = Sending.Transfer(list);

        var received = await Task.Run(() => sending.Receive()); // one owner, on another flow
        Assert.Same(list, received);

        // A second receive would mean two owners -- exactly the aliasing the type prevents.
        Assert.Throws<InvalidOperationException>(() => sending.Receive());
    }
}

public class StrictSendableModeTests
{
    [Fact(Timeout = 10000)]
    public async Task PolymorphicRecursion_TerminatesTheStructuralWalk()
    {
        // RecursiveBox<T> exposes RecursiveBox<List<T>>: every level is a FRESH closed type,
        // so the per-instance cycle set never repeats -- the same-definition path cap must cut
        // the walk. The closed type stores only readonly boxes, so strict mode accepts it.
        var f = new MemoFactory();
        var signal = f.CreateSignal(new RecursiveBox<int>());
        Assert.NotNull(await signal.Get());
    }

    [Fact(Timeout = 10000)]
    public async Task FiniteDeepGenericNesting_IsStillFullyChecked()
    {
        // Hand-written nesting repeats the definition but SHRINKS at every level: the
        // divergence cut must not fire, so the walk reaches the List at the bottom and strict
        // mode rejects the graph.
        var f = new MemoFactory();
        Assert.Throws<InvalidOperationException>(
            () => f.CreateSignal(new WrapBox<WrapBox<WrapBox<WrapBox<WrapBox<List<int>>>>>>()));

        // And the shrinking walk accepts a genuinely Sendable deep composition.
        var deepButClean = f.CreateSignal(new WrapBox<WrapBox<WrapBox<WrapBox<WrapBox<int>>>>>());
        Assert.NotNull(await deepButClean.Get());
    }

    [Fact(Timeout = 10000)]
    public async Task PolymorphicRecursion_WithAStoredParameter_IsRejectedNotAssumed()
    {
        // The divergent chain's SECOND level substitutes Value to List<int>: the walk must
        // inspect the first re-instantiation's members before cutting, or the shared mutable
        // list hides behind the cycle assumption.
        await Task.Yield(); // xunit requires async for Timeout, the termination backstop
        var f = new MemoFactory();
        Assert.Throws<InvalidOperationException>(() => f.CreateSignal(new RecursiveBoxWithValue<int>()));
    }

    [Fact]
    public void NativeIntegers_ArePrimitives_AndPassStrictMode()
    {
        // typeof(IntPtr).IsPrimitive is true, so the runtime walk's primitive short-circuit
        // accepts nint/nuint before any field reflection -- in lockstep with the analyzer's
        // System_IntPtr/System_UIntPtr acceptance.
        var f = new MemoFactory();
        Assert.NotNull(f.CreateSignal(nint.Zero));
        Assert.NotNull(f.CreateSignal(nuint.MinValue));
    }

    [Fact]
    public void DefaultFactory_Checks_AndDisableIsTheEscapeHatch()
    {
        // The Swift 6 language-mode analog (issue #145 part A4): strict IS the default.
        var f = new MemoFactory();
        Assert.Throws<InvalidOperationException>(() => f.CreateSignal(new List<int>()));

        var migrating = new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks);
        Assert.NotNull(migrating.CreateSignal(new List<int>()));
    }

    [Fact]
    public void StrictFactory_RejectsMutableTypes_OnEveryValueBearingNode()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks);

        Assert.Throws<InvalidOperationException>(() => f.CreateSignal(new List<int>()));
        Assert.Throws<InvalidOperationException>(() => f.CreateEagerRelativeSignal(new List<int>()));
        Assert.Throws<InvalidOperationException>(() => f.CreateMemoizR(async () => new List<int>()));
        Assert.Throws<InvalidOperationException>(() => f.CreateConcurrentMap<List<int>>(async _ => new List<int>()));
        Assert.Throws<InvalidOperationException>(() => f.CreateConcurrentMapReduce<List<int>>((a, b) => a, async _ => new List<int>()));
        // The race's resolver result R is handed to every racing child in parallel, so a
        // non-Sendable R must be rejected even when T is fine.
        Assert.Throws<InvalidOperationException>(() => f.CreateConcurrentRace<int, List<int>>(async () => new List<int>(), async (_, _) => 1));
    }

    [Fact]
    public void StrictFactory_TrustsSendableAttribute()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks);
        var signal = f.CreateSignal(new TrustedMutable());
        Assert.NotNull(signal);
    }

    [Fact(Timeout = 10000)]
    public async Task StrictFactory_AcceptsImmutableTypes_AndTheGraphWorks()
    {
        var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks);
        var v = f.CreateSignal(1);
        var p = f.CreateSignal(new SendablePerson("a", 1));
        var m = f.CreateMemoizR(async () => (await p.Get())!.Age + await v.Get());

        Assert.Equal(2, await m.Get());

        await v.Set(2);
        Assert.Equal(3, await m.Get());

        await p.Set(new SendablePerson("b", 10));
        Assert.Equal(12, await m.Get());
    }

    [Fact]
    public void StrictAndLaxFactories_CanShareOneKeyedContext()
    {
        // Strictness is a per-factory creation policy, not a context property.
        var key = $"strict-{Guid.NewGuid():N}";
        var strict = new MemoFactory(key, MemoFactoryOptions.StrictSendableChecks);
        var lax = new MemoFactory(key, MemoFactoryOptions.DisableSendableChecks);

        Assert.Same(strict.Context, lax.Context);
        Assert.Throws<InvalidOperationException>(() => strict.CreateSignal(new List<int>()));
        Assert.NotNull(lax.CreateSignal(new List<int>()));
    }
}

internal sealed record SendablePerson(string Name, int Age);

// Deliberately NOT sealed: exercises the synthesized `protected virtual Type EqualityContract`.
internal record SendableOpenRecord(string Name, int Age);

// Immutable, deliberately NON-sealed: the declared type passes creation-time checks...
// Polymorphic recursion: the member re-instantiates the declaration with a GROWING argument,
// so a naive per-closed-type cycle set never terminates (see the same-definition divergence
// cut in SendableChecker/SendableSymbolClassifier).
internal sealed class RecursiveBox<T>
{
    public RecursiveBox<List<T>>? Next { get; init; }
}

// A plain wrapper for FINITE hand-written nesting (WrapBox<WrapBox<...<List<int>>>>): each
// recursive step SHRINKS, so the divergence cut must let the walk reach the bottom.
internal sealed class WrapBox<T>
{
    public T? Value { get; init; }
}

// Polymorphic recursion that ALSO stores its parameter: the divergent chain's second level
// substitutes Value to List<int>, so the walk must inspect the first re-instantiation's own
// members before cutting.
internal sealed class RecursiveBoxWithValue<T>
{
    public RecursiveBoxWithValue<List<T>>? Next { get; init; }

    public T? Value { get; init; }
}

internal class OpenBase
{
    public string Name { get; init; } = "";
}

// ...and this is what an upcast smuggles past them: mutable subclass state.
internal sealed class MutableChild : OpenBase
{
    public int Mutable;
}

internal sealed class PlainInitDto
{
    public string Name { get; init; } = "";

    public int Age { get; init; }
}

// The mutable state leaks through a computed get-only property; there is no instance field.
internal sealed class LeakyComputedView
{
    private static readonly List<int> shared = [];

    public List<int> Snapshot => shared;
}

internal sealed record SendableLinkedNode(string Value, SendableLinkedNode? Next);

internal readonly record struct SendablePoint(int X, int Y);

internal sealed record MutableRecord
{
    public string Name { get; set; } = "";
}

internal sealed class HolderOfMutable
{
    public readonly List<int> Items = [];
}

// Deliberately full of mutable state: the attribute is the developer's thread-safety promise and
// must be trusted without structural checks.
[Sendable]
internal sealed class TrustedMutable
{
    public int Count { get; set; }
}

internal interface IHasItemsForExplicitLeak
{
    List<int> Items { get; }
}

// The mutable state is reachable ONLY through the interface cast: no instance fields, no
// visible property -- the explicit implementation is the whole leak surface.
internal sealed class ExplicitInterfaceLeak : IHasItemsForExplicitLeak
{
    private static readonly List<int> shared = [];

    List<int> IHasItemsForExplicitLeak.Items => shared;
}

internal interface IHasChangedForExplicitLeak
{
    event Action Changed;
}

internal sealed class ExplicitEventLeak : IHasChangedForExplicitLeak
{
    private static Action? shared;

    event Action IHasChangedForExplicitLeak.Changed
    {
        add => shared += value;
        remove => shared -= value;
    }
}

internal sealed class IndexerLeak
{
    private static readonly List<List<int>> shared = [];

    public List<int> this[int i] => shared[i];
}

internal sealed class HasCustomEvent
{
    private static Action? handlers;

    public event Action Changed
    {
        add => handlers += value;
        remove => handlers -= value;
    }
}
