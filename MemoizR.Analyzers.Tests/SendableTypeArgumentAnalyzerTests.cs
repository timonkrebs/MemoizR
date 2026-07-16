using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR001: every value-bearing factory creation is checked, the verdicts mirror the
// runtime SendableChecker (green-lists, [Sendable] trust, structural walk), type parameters get
// the benefit of the doubt, and the message names the offending member.
public class SendableTypeArgumentAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new SendableTypeArgumentAnalyzer());

    [Fact]
    public async Task MutableSignalType_IsFlagged_WithTheStructuralReason()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("List<int>", diagnostic.GetMessage());
        Assert.Contains("not Sendable", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ImmutableTypes_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public sealed record Person(string Name, int Age);

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(1);
                    f.CreateSignal("s");
                    f.CreateSignal(new Person("a", 1));
                    f.CreateSignal(ImmutableArray.Create(1, 2));
                    f.CreateMemoizR(async () => 1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NonSealedRecord_IsNotFlagged()
    {
        // A non-sealed record synthesizes `protected virtual Type EqualityContract { get; }`; the
        // get-only property-type check must not trip over it -- System.Type is abstract but
        // green-listed as known-immutable, in lockstep with the runtime checker.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public record OpenPerson(string Name, int Age);

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new OpenPerson("a", 1));
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task RecordWithSettableProperty_IsFlagged_NamingTheProperty()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public sealed record Mutable
            {
                public string Name { get; set; } = "";
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => new Mutable());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("'Name'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task FrozenCollections_AreNotFlagged()
    {
        // FrozenDictionary/FrozenSet are abstract by design (the runtime hands out internal
        // implementations): the known-definitions green-list must trust them BEFORE the
        // abstract-category rejection, in lockstep with the runtime checker.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Frozen;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new Dictionary<string, int> { ["a"] = 1 }.ToFrozenDictionary());
                    f.CreateSignal(new HashSet<int> { 1 }.ToFrozenSet());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UserTypeDeclaredInAFrameworkCollectionNamespace_IsFlagged()
    {
        // The collection green-list matches known framework definitions, not namespaces as
        // strings: a project's own type inside System.Collections.Concurrent goes through the
        // structural walk like any other type.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            namespace System.Collections.Concurrent
            {
                public class HomegrownCache
                {
                    public int Hits;
                }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new System.Collections.Concurrent.HomegrownCache());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("Hits", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ExplicitInterfaceProperty_OfMutableType_IsFlagged()
    {
        // Explicit implementations are declared private but reachable through a cast to the
        // interface: the exposed List is shared mutable state like any visible property.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public interface IHasItems
            {
                List<int> Items { get; }
            }

            public sealed class ExplicitLeak : IHasItems
            {
                private static readonly List<int> shared = new();

                List<int> IHasItems.Items => shared;
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new ExplicitLeak());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("Items", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ShadowedFactoryLookalike_DoesNotDrawDiagnostics()
    {
        // A source-shadowed MemoizR.MemoFactory is NOT the reactive factory: its APIs publish
        // nothing into a graph, so the rules must not classify its invocations as creations.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;

            namespace MemoizR
            {
                public class MemoFactory
                {
                    public T CreateSignal<T>(T value) => value;
                }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoizR.MemoFactory();
                    f.CreateSignal(new List<int>()); // not a graph value: must not be MZR001
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ExplicitInterfaceEvent_IsFlagged()
    {
        // Declared private, reachable through the interface cast: subscribing mutates shared
        // state, exactly like a visible event.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public interface IHasChanged
            {
                event EventHandler Changed;
            }

            public sealed class ExplicitEventLeak : IHasChanged
            {
                private static EventHandler? shared;

                event EventHandler IHasChanged.Changed
                {
                    add => shared += value;
                    remove => shared -= value;
                }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new ExplicitEventLeak());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("Changed", diagnostic.GetMessage());
    }

    [Fact]
    public async Task GetOnlyIndexer_OfMutableType_IsFlagged()
    {
        // A computed get-only indexer hands out the same shared mutable state as a get-only
        // property; there is no setter and no field for any other rule to catch.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public sealed class IndexerLeak
            {
                private static readonly List<List<int>> shared = new();

                public List<int> this[int i] => shared[i];
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new IndexerLeak());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("indexer", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SourceShadowedSendableAttribute_IsNotTrusted()
    {
        // A source-declared MemoizR.SendableAttribute binds over the library's (with a conflict
        // warning); trusting it would leave MZR001 silent for a type strict runtime mode
        // rejects, since the runtime checks typeof identity of the REAL attribute.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            namespace MemoizR
            {
                public sealed class SendableAttribute : System.Attribute
                {
                }
            }

            [MemoizR.Sendable]
            public sealed class ClaimsToBeSafe
            {
                public int State;
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new ClaimsToBeSafe());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("State", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SourceDeclaredKnownImmutableLookalike_IsFlagged()
    {
        // Same rule as the collection lookalike below, applied to the known-immutable
        // green-list: a source-declared System.Uri binds over the BCL one and must go through
        // the structural walk (the runtime's typeof identity rejects it too).
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            namespace System
            {
                public sealed class Uri
                {
                    public int State;
                }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new System.Uri());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("State", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SourceDeclaredCollectionLookalike_IsFlagged()
    {
        // An exact-name lookalike declared in source (source wins over metadata on a name
        // clash, so this IS the type the creation binds to) must not be blessed by the
        // green-list: the runtime's typeof-identity check rejects it, and the analyzer must
        // stay in lockstep and let the structural walk flag it.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            namespace System.Collections.Concurrent
            {
                public class ConcurrentQueue<T>
                {
                    public T? Head;
                }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new System.Collections.Concurrent.ConcurrentQueue<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("Head", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ImmutableCollectionBuilder_IsFlagged_NotBlessedByNamespace()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(ImmutableList.CreateBuilder<int>()); // Builder is mutable
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task TypeWithVisibleEvent_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public sealed class HasEvent { public event Action? Changed; }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new HasEvent()); // subscribing mutates the shared instance
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task GetOnlyPropertyOfNonSendableType_IsFlagged()
    {
        // No backing field for the field walk to see (computed get-only property), so the property
        // TYPE must be checked -- this is what catches a metadata `public List<int> Items { get; }`
        // whose private backing field the compiler does not import.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public sealed class Exposes { public List<int> Items => new(); }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new Exposes());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("Items", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SendableAttribute_IsTrusted()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            [Sendable]
            public sealed class TrustedMutable
            {
                public int Count { get; set; }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new TrustedMutable());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UnboundTypeParameter_GetsTheBenefitOfTheDoubt()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public Signal<T> Make<T>(MemoFactory f, T value) => f.CreateSignal(value);
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ConcurrentRace_ResolverResult_IsChecked()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateConcurrentRace<int, List<int>>(
                        async () => new List<int>(),
                        async (_, _) => 1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("List<int>", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ActorEngineCreations_AreChecked()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateActorSignal(new List<int>());
                    f.CreateActorMemoizR(async () => new Dictionary<string, int>());
                    f.CreateActorSignal(1); // fine
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR001", d.Id));
    }

    [Fact]
    public async Task DisableSendableChecks_Factory_IsExemptWhereverItsConstructionIsVisible()
    {
        // The runtime accepts this exact per-factory escape hatch; under the Error default the
        // build must accept it too. Positive evidence counts through every documented shape:
        // an inline receiver, a local initializer (including flag combinations), and a
        // same-file static field initializer -- for instance methods and the structured
        // extension methods alike.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                private static readonly MemoFactory Lax = new(options: MemoFactoryOptions.DisableSendableChecks);

                public void M()
                {
                    new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks).CreateSignal(new List<int>());

                    var lax = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks | MemoFactoryOptions.DisableSendableChecks);
                    lax.CreateSignal(new List<int>());
                    lax.CreateConcurrentMap<List<int>>(async _ => new List<int>()); // extension-method receiver

                    Lax.CreateSignal(new List<int>());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ReassignedFactoryLocal_DoesNotInheritTheInitializerOptOut()
    {
        // The initializer opted out, but the local was since repointed at a strict factory:
        // the runtime WILL throw on this creation, so the build must keep saying so.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks);
                    f = new MemoFactory();
                    f.CreateSignal(new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task ParenthesizedOptOutReceivers_KeepTheOptOut()
    {
        // Parentheses are pure syntax: the receiver operation is the creation itself.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    (new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks)).CreateSignal(new List<int>());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task BlazorFluentOptOutFactory_IsStillExempt()
    {
        // AddBlazorDispatcher mirrors AddWpfDispatcher: it returns the SAME factory via
        // AddExecutor, so the opt-out evidence one hop up the chain still holds.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;
            using Microsoft.AspNetCore.Components;

            public class C
            {
                public void M(Dispatcher dispatcher)
                {
                    new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks)
                        .AddBlazorDispatcher(dispatcher)
                        .CreateSignal(new List<int>());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FluentlyConfiguredOptOutFactory_IsStillExempt()
    {
        // AddExecutor/AddTimeProvider mutate and return the SAME factory, so the opt-out
        // evidence sits one hop up the fluent chain -- the runtime uses the opted-out factory
        // and accepts the value, and the build must agree.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks)
                        .AddExecutor(new SynchronizationContextExecutor(new SynchronizationContext()))
                        .CreateSignal(new List<int>());

                    var lax = new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks);
                    lax.AddTimeProvider(TimeProvider.System).CreateSignal(new List<int>());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FluentlyConfiguredFactory_StoredInALocal_KeepsTheOptOut()
    {
        // The initializer's value is the fluent call's result -- which IS the factory the
        // initializer created (AddTimeProvider returns its receiver) -- so the peeling applies
        // to initializers exactly like direct receivers.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var lax = new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks)
                        .AddTimeProvider(TimeProvider.System);
                    lax.CreateSignal(new List<int>());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task GenericPassthroughsReturningAFactory_AreNotFollowed()
    {
        // Untrack<T> returns its DELEGATE's result -- here a strict factory -- so following it
        // back to the lax receiver would hide an error the runtime throws. Only the named
        // fluent methods (which return their own receiver) are followed.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks)
                        .Untrack(() => new MemoFactory())
                        .CreateSignal(new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task InArguments_DoNotRevokeTheOptOut()
    {
        // `in` is a read-only pass: the callee cannot repoint the local, so the initializer
        // still proves which factory runs the creation.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var lax = new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks);
                    Inspect(in lax);
                    lax.CreateSignal(new List<int>());
                }

                private static void Inspect(in MemoFactory factory)
                {
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task VirtualFactoryProperty_IsNotTrusted_DispatchMayLandElsewhere()
    {
        // A derived override can hand back a strict factory the base initializer never saw:
        // only a getter that cannot dispatch elsewhere may vouch for its initializer.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Base
            {
                public virtual MemoFactory Lax { get; } = new(options: MemoFactoryOptions.DisableSendableChecks);

                public void M()
                {
                    Lax.CreateSignal(new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task ConditionalAccessReceiver_KeepsTheOptOut()
    {
        // `lax?.CreateSignal(...)` either does not run or runs on the lax factory -- the
        // conditional-access placeholder must resolve to the visible initializer either way.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    MemoFactory? lax = new(options: MemoFactoryOptions.DisableSendableChecks);
                    lax?.CreateSignal(new List<int>());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NullForgivingFactoryReceiver_KeepsTheOptOut()
    {
        // `lax!.CreateSignal(...)` is the same lax factory; the null-forgiving operator must
        // not hide the receiver from the opt-out resolution.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    MemoFactory? lax = new(options: MemoFactoryOptions.DisableSendableChecks);
                    lax!.CreateSignal(new List<int>());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task PartialTypeFactoryField_IsNotTrusted_TheOtherFileMayReassignIt()
    {
        // A readonly field's initializer can be overwritten by a static constructor, and a
        // partial type can keep that constructor in ANOTHER file: members of types split
        // across files are not trusted, whatever the visible initializer says.
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            [
                """
                using System.Collections.Generic;
                using MemoizR;

                public partial class C
                {
                    private static readonly MemoFactory Lax = new(options: MemoFactoryOptions.DisableSendableChecks);

                    public void M()
                    {
                        Lax.CreateSignal(new List<int>());
                    }
                }
                """,
                """
                public partial class C
                {
                    static C()
                    {
                        Lax = new MemoizR.MemoFactory(); // strict: the initializer never survives
                    }
                }
                """,
            ],
            new SendableTypeArgumentAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task RefAliasedFactoryLocal_DoesNotKeepTheInitializerOptOut()
    {
        // `ref var r = ref f` lets any later write repoint the local without naming it, so a
        // ref escape revokes the initializer's authority like a direct reassignment.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks);
                    ref var r = ref f;
                    r = new MemoFactory();
                    f.CreateSignal(new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task ConditionallyLaxOptions_AreNotADefiniteOptOut()
    {
        // On the false path the factory is strict and the creation throws at runtime: a mere
        // MENTION of the flag in a non-constant options expression is not definite evidence.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool useLax)
                {
                    new MemoFactory(options: useLax ? MemoFactoryOptions.DisableSendableChecks : MemoFactoryOptions.None)
                        .CreateSignal(new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task OtherOptions_AndUnresolvableReceivers_StayChecked()
    {
        // Conservative direction: only POSITIVE evidence of DisableSendableChecks exempts a
        // creation. Other flags do not, and a factory the analyzer cannot see behind (here a
        // parameter) keeps the checks on even if its construction elsewhere opted out.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(MemoFactory fromElsewhere)
                {
                    var strict = new MemoFactory(options: MemoFactoryOptions.ValidateWrittenValues);
                    strict.CreateSignal(new List<int>());
                    fromElsewhere.CreateSignal(new List<int>());
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR001", d.Id));
    }

    [Fact]
    public async Task ConcurrentMap_ElementType_IsChecked()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateConcurrentMap<List<int>>(async _ => new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    // ADR 0007's process layer: the action payload crosses to the detached run flow and the
    // optimistic view publishes T across flows -- both are runtime-checked in strict mode, so
    // the build-time mirror must cover them like every other value-bearing factory.
    [Fact]
    public async Task ProcessLayerFactories_MutableTypeArguments_AreFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using MemoizR;

            public sealed class ListSource : IStateGetR<List<int>>
            {
                public Task<List<int>> Get() => Task.FromResult(new List<int>());
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateAction<List<int>>((p, ctx) => Task.CompletedTask);
                    f.CreateOptimistic<List<int>>(new ListSource());
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR001", d.Id));
    }

    [Fact]
    public async Task ProcessLayerFactories_ImmutableTypeArguments_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateAction<string>((p, ctx) => Task.CompletedTask);
                    f.CreateOptimistic<int>(f.CreateSignal(1));
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
}
