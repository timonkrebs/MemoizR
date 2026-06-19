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
    public void ImmutableAndConcurrentCollections_AreSendable_WhenTheirElementsAre(Type type)
    {
        Assert.True(SendableChecker.IsSendable(type, out var reason), reason);
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
    public void EnsureSendable_Throws_WithReasonAndFixGuidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SendableChecker.EnsureSendable(typeof(List<int>)));
        Assert.Contains("not Sendable", ex.Message);
        Assert.Contains("[MemoizR.Sendable]", ex.Message);
    }
}

public class StrictSendableModeTests
{
    [Fact]
    public void DefaultFactory_DoesNotCheck()
    {
        var f = new MemoFactory();
        var signal = f.CreateSignal(new List<int>()); // compatibility: lax by default
        Assert.NotNull(signal);
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
        var lax = new MemoFactory(key);

        Assert.Same(strict.Context, lax.Context);
        Assert.Throws<InvalidOperationException>(() => strict.CreateSignal(new List<int>()));
        Assert.NotNull(lax.CreateSignal(new List<int>()));
    }
}

internal sealed record SendablePerson(string Name, int Age);

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
