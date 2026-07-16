using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR004: an optimistic patch's closure captures cross flows (the patch re-runs
// inside the view's computation on whichever flow pulls), so non-Sendable-typed captures and
// reads of writable enclosing-object state are flagged; Sendable snapshots -- the idiomatic
// payload capture -- are not.
public class OptimisticPatchCaptureAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new OptimisticPatchCaptureAnalyzer());

    [Fact]
    public async Task NonSendableLocalCapture_IsFlagged_WithTheStructuralReason()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var shared = new List<int>();
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + shared.Count);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("shared", diagnostic.GetMessage());
        Assert.Contains("List<int>", diagnostic.GetMessage());
        Assert.Contains("not Sendable", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SendableCaptures_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public sealed record Item(string Name);

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var label = "x";
                    var item = new Item("a");
                    // The idiomatic shape: the payload and immutable snapshots are captured, and
                    // the patch's own parameter is used freely.
                    f.CreateAction<string>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + p.Length + label.Length + item.Name.Length);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task EnclosingObjectState_WritableOrNonSendable_IsFlagged_ReadonlyImmutableIsNot()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private int counter;                        // writable field: flagged
                private readonly List<int> cache = new();   // readonly, but the List is shared: flagged
                private readonly string name = "n";         // readonly + Sendable: fine

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + counter + cache.Count + name.Length);
                    });
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR004", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("counter") && d.GetMessage().Contains("writable state"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("cache") && d.GetMessage().Contains("not Sendable"));
    }

    [Fact]
    public async Task EnclosingProperties_SettableIsFlagged_GetOnlySendableIsNot()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public int Threshold { get; set; }   // settable: flagged
                public string Tag => "t";            // computed get-only, Sendable type: fine

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Threshold + Tag.Length);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("Threshold", diagnostic.GetMessage());
        Assert.Contains("writable state", diagnostic.GetMessage());
    }

    [Fact]
    public async Task EachCapturedSymbol_IsReportedOnce_PerPatch()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var shared = new List<int>();
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + shared.Count + shared.Capacity);
                    });
                }
            }
            """);

        Assert.Single(diagnostics);
    }
}
