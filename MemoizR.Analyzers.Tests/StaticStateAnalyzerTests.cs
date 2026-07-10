using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR004 (the SE-0412 analog): in files that use MemoizR, a static must be an
// immutable slot of a Sendable type -- mutable slots (non-readonly fields, settable properties,
// events) and readonly slots of mutable types are flagged; consts, Sendable readonly statics,
// and MemoizR nodes/factories (which are [Sendable] by design) are not. Files without a MemoizR
// using directive are out of the rule's mandate.
public class StaticStateAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new StaticStateAnalyzer());

    [Fact]
    public async Task MutableStaticSlots_AreFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
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
            }
            """);

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR004", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'counter'"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'Label'"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'Changed'"));
    }

    [Fact]
    public async Task ReadonlyStaticOfMutableType_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private static readonly List<int> Cache = new(); // readonly slot, mutable object

                public void M()
                {
                    var f = new MemoFactory();
                    Cache.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'Cache'", diagnostic.GetMessage());
        Assert.Contains("List", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SendableStatics_AndConsts_AndNodes_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public class C
            {
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
            }
            """);

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

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
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

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'Cache'", diagnostic.GetMessage());
        Assert.Contains("type parameter", diagnostic.GetMessage());
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

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
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

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'Cache'", diagnostic.GetMessage());
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
}
