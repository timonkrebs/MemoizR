using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR002: writes from inside a computation to a captured local/parameter, a field
// of the enclosing type, or a static field are flagged; the computation's own locals (including
// those of nested non-computation lambdas) and plain reads are not.
public class CapturedMutationAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new CapturedMutationAnalyzer());

    [Fact]
    public async Task WriteToCapturedLocal_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    int counter = 0;
                    f.CreateMemoizR(async () => { counter++; return counter; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("captured local 'counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ComputationOwnLocals_AndReadsOfCapturedState_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    int config = 5;
                    f.CreateMemoizR(async () =>
                    {
                        int local = config; // read of captured state: idiomatic, not flagged
                        local++;
                        return local;
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task WriteToStaticField_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static int total;

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { total = 5; return total; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("static field 'total'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task WriteToEnclosingInstanceProperty_AndStaticProperty_AreFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private int Hits { get; set; }
                private static int Total { get; set; }

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { Hits++; Total = 5; return Hits + Total; });
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("property 'Hits'"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("static property 'Total'"));
    }

    [Fact]
    public async Task WriteToEnclosingInstanceField_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { hits++; return hits; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("field 'hits'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ReactionAction_WritingCapturedLocal_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var m = f.CreateMemoizR(async () => await v.Get());
                    int sum = 0;
                    f.BuildReaction().CreateReaction(m, value => sum = value);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("captured local 'sum'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task FactoryLevelCreateReaction_WritingCapturedLocal_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var m = f.CreateMemoizR(async () => await v.Get());
                    int sum = 0;
                    f.CreateReaction(m, value => sum = value); // factory-level sugar, not the builder
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("captured local 'sum'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task WriteToFieldOfCapturedStructLocal_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public struct Counter { public int Value; }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var counter = new Counter();
                    f.CreateMemoizR(async () => { counter.Value++; return counter.Value; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("captured local 'counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task WriteToMemberOfEnclosingStructField_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public struct Counter { public int Value; }

            public class C
            {
                private Counter counter;
                private static Counter StaticCounter;

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { this.counter.Value++; StaticCounter.Value++; return 1; });
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("field 'counter'"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("static field 'StaticCounter'"));
    }

    [Fact]
    public async Task NestedDeconstructionOfCapturedLocal_IsFlagged()
    {
        // The captured local sits in the NESTED tuple: `(a, (shared, c))` writes it just as
        // surely as a top-level element, so the flattening must recurse.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    int shared = 0;
                    f.CreateMemoizR(async () =>
                    {
                        int a;
                        (a, (shared, var c)) = (1, (2, 3));
                        return a + shared + c;
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("captured local 'shared'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task EventSubscriptionInComputation_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private event Action Changed;
                private static event Action StaticChanged;

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { Changed += () => {}; StaticChanged += () => {}; return 1; });
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("event 'Changed'"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("static event 'StaticChanged'"));
    }

    [Fact]
    public async Task MethodGroupComputation_WritingEnclosingField_IsFlagged()
    {
        // `CreateMemoizR(Compute)` makes Compute's body a computation just like a lambda; the
        // method's own locals stay unflagged (declared inside the resolved scope).
        var diagnostics = await AnalyzeAsync("""
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                private int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(Compute);
                }

                private async Task<int> Compute()
                {
                    int local = 0;
                    local++;
                    hits++;
                    return hits + local;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("field 'hits'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task LocalFunctionComputation_WritingCapturedLocal_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    int counter = 0;
                    f.CreateMemoizR(Compute);

                    async Task<int> Compute() { counter++; return counter; }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("captured local 'counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task LocalOfNestedNonComputationLambda_IsNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Linq;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => Enumerable.Range(0, 3).Select(i => { int x = i; x++; return x; }).Sum());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NestedComputation_IsReportedOnce_ByItsOwnAnalysis()
    {
        // A creation nested inside another computation: the write belongs to the INNER lambda's
        // analysis; the outer walk must prune at the nested host instead of double-reporting.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    int counter = 0;
                    f.CreateMemoizR(async () =>
                    {
                        var inner = f.CreateMemoizR(async () => { counter++; return counter; });
                        return await inner.Get();
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("captured local 'counter'", diagnostic.GetMessage());
    }
}
