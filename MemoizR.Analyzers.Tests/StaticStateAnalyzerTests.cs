using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR004 (the SE-0412 analog): in files that use MemoizR, a static must be an
// immutable slot of a Sendable type -- mutable slots (non-readonly fields, settable properties,
// events) and readonly slots of mutable types are flagged; consts, Sendable readonly statics,
// and MemoizR nodes/factories (which are [Sendable] by design) are not. Files without a MemoizR
// using directive are out of the rule's mandate.
public class StaticStateAnalyzerTests
{
    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new StaticStateAnalyzer());

    private static Task<ImmutableArray<Diagnostic>> InClassC(string members, string usings = "using System.Collections.Generic;\nusing MemoizR;")
        => AnalyzeAsync(AnalyzerTestHarness.InClassC(members, usings));

    [Fact]
    public async Task StaticAbstractInterfaceContracts_OwnNoSlot()
    {
        // A static abstract member is only a CONTRACT: the interface holds no storage, so
        // there is nothing shared to flag -- the implementing types own (and answer for)
        // the actual slots.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public interface IState<T>
            {
                static abstract List<int> Items { get; set; }
            }
            """);

        Assert.Empty(diagnostics.Where(d => d.Id == "MZR004"));
    }

    [Fact]
    public async Task MutableStaticSlots_AreFlagged()
    {
        var diagnostics = await InClassC("""
                private static int counter; // mutable slot, even though int is Sendable
                public static string Label { get; set; } = ""; // settable slot
                public static event Action? Changed; // subscription surface

                public void M()
                {
                    var f = new MemoFactory();
                    counter++;
                    Label = "x";
                    Changed?.Invoke();
                }
            """, "using System;\nusing MemoizR;");

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR004", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'counter'"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'Label'"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'Changed'"));
    }

    [Fact]
    public async Task ReadonlyStaticOfMutableType_IsFlagged()
    {
        var diagnostics = await InClassC("""
                private static readonly List<int> Cache = new(); // readonly slot, mutable object

                public void M()
                {
                    var f = new MemoFactory();
                    Cache.Add(1);
                }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("'Cache'", diagnostic.GetMessage());
        Assert.Contains("List", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SendableStatics_AndConsts_AndNodes_AreNotFlagged()
    {
        var diagnostics = await InClassC("""
                private const int Limit = 3;
                private static readonly string Name = "x";
                private static readonly ImmutableArray<int> Seeds = ImmutableArray.Create(1, 2);

                // The library's own model: nodes and factories are [Sendable] by design, so the
                // fix suggestion ("lift it into a Signal") itself passes the rule.
                private static readonly MemoFactory Factory = new();
                private static readonly Signal<int> Counter = Factory.CreateSignal(0);

                public void M()
                {
                    _ = Limit + Name.Length + Seeds.Length;
                    _ = Counter.Get();
                }
            """, "using System.Collections.Immutable;\nusing MemoizR;");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task TypeParameterStatics_AreUnverifiable_AndFlagged()
    {
        // MZR001 trusts unbound type parameters because the closed instantiation is checked at
        // its own creation site; a static has no such site -- every closed C<T> mints a fresh
        // process-wide slot (C<List<int>>.Cache is a shared mutable object graph) no rule ever
        // sees again -- so here T is unverifiable, not trusted.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C<T> where T : new()
            {
                private static readonly T Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("'Cache'", diagnostic.GetMessage());
        Assert.Contains("type parameter", diagnostic.GetMessage());
    }

    [Fact]
    public async Task NestedTypeParameterStatics_AreUnverifiable_TrustedNodesAreTheShield()
    {
        // The classifier accepts type parameters recursively (right for MZR001), so
        // ImmutableArray<T> would pass while C<List<int>>.Cache is a process-wide immutable
        // wrapper over a mutable graph with no later check. A [Sendable]-trusted node is the
        // one shield: Signal<T> is internally synchronized for any T, and the closed T IS
        // checked later, at the CreateSignal call that built the instance.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public class C<T>
            {
                private static readonly ImmutableArray<T> Cache = ImmutableArray<T>.Empty;
                private static readonly Signal<T>? Node = null;

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache.Length + (Node is null ? 0 : 1);
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("'Cache'", diagnostic.GetMessage());
        Assert.Contains("type parameter", diagnostic.GetMessage());
    }

    [Fact]
    public async Task OuterGenericParameters_InNestedMemberTypes_AreUnverifiable()
    {
        // A nested type carries the OUTER T on its members, not in its own argument list:
        // C<List<int>>.Cache is still a process-wide object graph over a mutable list, so the
        // member walk must find the T the shared classifier would exempt.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C<T>
            {
                private sealed class Holder
                {
                    public T? Value { get; init; }
                }

                private static readonly Holder Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("'Cache'", diagnostic.GetMessage());
        Assert.Contains("type parameter", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InheritedMemberTypes_CarryTheOuterParameter_AndAreUnverifiable()
    {
        // The member walk follows the base chain like the classifier: an inherited member
        // stores the outer T exactly like a declared one.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C<T>
            {
                private class Base
                {
                    public T? Value { get; init; }
                }

                private sealed class Holder : Base
                {
                }

                private static readonly Holder Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("'Cache'", diagnostic.GetMessage());
        Assert.Contains("type parameter", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ComputedMemberProperties_StoreNothing_AndDoNotPoisonTheSlot()
    {
        // `public T New => default!` holds no slot: the holder static contains no T reference,
        // the member-level analog of the top-level computed-getter exemption.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C<T>
            {
                private sealed class Holder
                {
                    public T New => default!;
                }

                private static readonly Holder Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FiniteDeepGenericNesting_IsStillFullyChecked()
    {
        // Hand-written nesting repeats the definition but SHRINKS at every level: the
        // divergence cut must not fire, so the classifier reaches the List at the bottom.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public sealed class WrapBox<T>
            {
                public T? Value { get; init; }
            }

            public class C
            {
                private static readonly WrapBox<WrapBox<WrapBox<WrapBox<WrapBox<List<int>>>>>> Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("List", diagnostic.GetMessage());
    }

    [Fact]
    public async Task UnmanagedTypeParameters_StayUnverifiable_PointersSatisfyTheConstraint()
    {
        // `where T : unmanaged` is NOT a proof: an `unsafe struct` with a pointer field
        // satisfies it, and a copied pointer still aliases writable memory (the runtime
        // rejects pointer fields). Only `where T : Enum` guarantees immutable values or
        // immutable boxes for every instantiation; a plain `struct` constraint promises even
        // less -- a struct can carry references to mutable objects.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class UnmanagedCache<T> where T : unmanaged
            {
                private static readonly T Zero = default;

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Zero;
                }
            }

            public class StructCache<T> where T : struct
            {
                private static readonly T Zero = default;

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Zero;
                }
            }

            public class EnumCache<T> where T : System.Enum
            {
                private static readonly T? Zero = default;

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Zero;
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR004", d.Id));
        Assert.All(diagnostics, d => Assert.Contains("type parameter", d.GetMessage()));
    }

    [Fact]
    public async Task NestedTypes_ResubstitutedThroughContainingArguments_AreStillWalked()
    {
        // Outer<int>.Holder and Outer<T>.Holder share a DECLARATION but differ through the
        // containing type's arguments: after walking the first, the second must still be
        // inspected, or the outer T (a List<int> in C<List<int>>) hides on Holder.Value.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class Outer<U>
            {
                public sealed class Holder
                {
                    public U? Value { get; init; }
                }
            }

            public class C<T>
            {
                private sealed class Pair
                {
                    public Outer<int>.Holder? A { get; init; }

                    public Outer<T>.Holder? B { get; init; }
                }

                private static readonly Pair Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("type parameter", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SiblingInstantiations_DoNotExhaustTheMemberWalk()
    {
        // Two safe sibling instantiations of the same nested declaration are not recursion:
        // the third, carrying the outer T through its containing type, must still be walked.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class Outer<U>
            {
                public sealed class Holder
                {
                    public U? Value { get; init; }
                }
            }

            public class C<T>
            {
                private sealed class Triple
                {
                    public Outer<int>.Holder? A { get; init; }

                    public Outer<string>.Holder? B { get; init; }

                    public Outer<T>.Holder? C { get; init; }
                }

                private static readonly Triple Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("type parameter", diagnostic.GetMessage());
    }

    [Fact]
    public async Task PolymorphicRecursion_WithAStoredParameter_IsRejectedNotAssumed()
    {
        // The divergent chain's SECOND level substitutes Value to List<int>: the classifier
        // must inspect the first re-instantiation's members before cutting.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public sealed class Box<T>
            {
                public Box<List<T>>? Next { get; init; }

                public T? Value { get; init; }
            }

            public class C
            {
                private static readonly Box<int> Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("List", diagnostic.GetMessage());
    }

    [Fact]
    public async Task PolymorphicRecursion_Terminates_AndTheClosedSlotIsAccepted()
    {
        // Box<T> exposing Box<List<T>> constructs a FRESH closed symbol per level: the member
        // walk (once per declaration) and the classifier's same-definition path cap must both
        // terminate. The closed Box<int> stores only Boxes -- all readonly, no mutable leaf --
        // so MZR004 accepts the slot.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public sealed class Box<T>
            {
                public Box<List<T>>? Next { get; init; }
            }

            public class C
            {
                private static readonly Box<int> Cache = new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MZR004");
    }

    [Fact]
    public async Task TypeParameters_BuriedArbitrarilyDeep_AreStillFound()
    {
        // The walk has no depth cliff: a declared type reference is a finite tree, so the
        // recursion terminates on its own -- a cap would have had to fail open (a parameter
        // one level past it silently trusted) or misreport deep concrete types.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public class C<T>
            {
                private static readonly ImmutableArray<ImmutableArray<ImmutableArray<ImmutableArray<ImmutableArray<ImmutableArray<T>>>>>> Cache = default;

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("type parameter", diagnostic.GetMessage());
    }

    [Fact]
    public async Task GlobalUsings_PutEveryFileInScope()
    {
        // Centralized `global using MemoizR;` (a separate GlobalUsings.cs) puts MemoizR in
        // scope for every file, so the per-FILE using check must not exempt such projects: the
        // static's own file has no using directive at all here.
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            [
                """
                global using MemoizR;
                """,
                """
                using System.Collections.Generic;

                public class C
                {
                    private static readonly List<int> Cache = new();

                    public void M()
                    {
                        var f = new MemoFactory();
                        Cache.Add(1);
                    }
                }
                """,
            ],
            new StaticStateAnalyzer());

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("'Cache'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ComputedStaticGetters_AreNotState()
    {
        // An expression-bodied getter owns no static slot: each call returns a fresh value,
        // and a getter handing out OTHER static state is flagged at that state's declaration.
        var diagnostics = await InClassC("""
                public static List<int> NewList => new();

                public void M()
                {
                    var f = new MemoFactory();
                    _ = NewList;
                }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ReadonlyStaticOfANonSealedSendableType_GetsTheSmugglingHint()
    {
        // The declared type passes the slot rule, but a mutable subclass behind the upcast is
        // process-wide state with no creation site where MZR006 would otherwise hint and no
        // runtime write validation ever seeing the slot: the Info hint fires at the static.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public record OpenPerson(string Name);

            public class C
            {
                private static readonly OpenPerson Cache = new("a");

                public void M()
                {
                    var f = new MemoFactory();
                    _ = Cache;
                }
            }
            """);

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR006");
        Assert.Contains("OpenPerson", diagnostic.GetMessage());
        Assert.Contains("static slot publishes unchecked", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetOnlyStaticProperties_AreSettableSlots()
    {
        // A settable static property is a process-wide write surface no matter where the
        // value lands -- the get+set arm never required a backing slot, and dropping the
        // getter does not make the surface less writable.
        var diagnostics = await InClassC("""
                public static int Sink { set { _ = value; } }
            """, "using MemoizR;");

        var diagnostic = AnalyzerTestHarness.AssertSingle(diagnostics, "MZR004");
        Assert.Contains("settable", diagnostic.GetMessage());
    }

    [Fact]
    public async Task FilesWithoutMemoizRUsing_AreOutOfScope()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;

            public class Unrelated
            {
                public static List<int> Anything = new(); // mutable static, but not MemoizR's mandate
            }
            """);

        Assert.Empty(diagnostics);
    }


    [Fact]
    public async Task ThreadStaticFields_AreNotSharedSlots()
    {
        // Every thread gets its own storage: concurrent flows on different threads never
        // touch the same slot, so there is no shared mutable state to flag.
        var diagnostics = await InClassC("""
                [ThreadStatic]
                private static int depth;

                public void M()
                {
                    var f = new MemoFactory();
                    depth++;
                }
            """, "using System;\nusing MemoizR;");

        Assert.Empty(diagnostics);
    }
}
