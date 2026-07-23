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
    public async Task MutatingMethodCall_OnCapturedStructReceiver_IsFlagged()
    {
        // `counter.Increment()` writes the captured local's storage exactly like
        // `counter.Value++`; readonly members, the object virtuals, and the computation's own
        // locals stay unflagged.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public struct Counter
            {
                public int Value;
                public void Increment() => Value++;
                public readonly int Peek() => Value;
            }

            public class C
            {
                private readonly Counter frozen;

                public void M()
                {
                    var f = new MemoFactory();
                    var counter = new Counter();
                    f.CreateMemoizR(async () =>
                    {
                        counter.Increment(); // mutates shared storage: flagged
                        _ = counter.Peek(); // readonly member: cannot mutate
                        _ = counter.ToString(); // object virtual: not flagged
                        frozen.Increment(); // readonly field: runs on a defensive copy, no shared write
                        var mine = new Counter();
                        mine.Increment(); // the computation's own local
                        return counter.Value;
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("captured local 'counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task MutatingCall_OnRefReturnPropertyReceiver_IsFlagged()
    {
        // A ref-returning property hands out the storage itself (no defensive copy), so the
        // mutating call writes the enclosing object's field on every recompute.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public struct Counter
            {
                public int Value;
                public void Increment() => Value++;
            }

            public class C
            {
                private Counter counter;

                private ref Counter CounterRef => ref counter;

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () =>
                    {
                        CounterRef.Increment();
                        return counter.Value;
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("'CounterRef'", diagnostic.GetMessage());
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
    public async Task SideEffectInANestedCreationsOrdinaryArgument_IsFlagged()
    {
        // The nested creation's LABEL argument is evaluated during the OUTER computation, so a
        // write there is the outer computation's shared mutation; only the nested delegate
        // bodies belong to the nested invocation's own analysis.
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
                        var inner = f.CreateMemoizR("m" + ++counter, async () => 1);
                        return await inner.Get();
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("captured local 'counter'", diagnostic.GetMessage());
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

    // Apply's patch is a computation host (ADR 0007): it re-runs inside the view's computation
    // on arbitrary flows, so a captured-state write in one is exactly this rule's data race.
    [Fact]
    public async Task OptimisticPatch_WritingCapturedLocal_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    int applied = 0;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { applied++; return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("captured local 'applied'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task OptimisticPatch_PureProjection_IsNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + p);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // The write hides behind a local function declared outside the computation: its closure is
    // the computation's environment, so the chased `applied++` is the same data race as the
    // inline form (and MZR004 cannot carry it -- the int is Sendable; the WRITE is the race).
    // The helper's OWN local stays exempt: it is recreated per call.
    [Fact]
    public async Task OptimisticPatch_WriteHiddenInALocalHelper_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var applied = 0;
                    int Next()
                    {
                        var tmp = 0;
                        tmp++;
                        applied++;
                        return applied + tmp;
                    }
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Next());
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("captured local 'applied'", diagnostic.GetMessage());
    }

    // nameof(Next) is a compile-time string: the local function is neither invoked nor stored,
    // so its body must not be chased.
    [Fact]
    public async Task NameofALocalHelper_DoesNotChaseIt()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var applied = 0;
                    int Next() { applied++; return applied; }
                    f.CreateMemoizR(async () => nameof(Next).Length);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // A method invoked on THIS writes the enclosing object's state exactly like the inline
    // form; a method on some OTHER receiver mutates that object instead -- a captured-
    // reference mutation, deliberately MZR001's territory.
    [Fact]
    public async Task OptimisticPatch_WriteHiddenInAThisMethod_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private int counter;

                private void Inc() => counter++;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { Inc(); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("field 'counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task OptimisticPatch_HelperOnAnotherReceiver_IsNotChased()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class Helper
            {
                private int count;

                public void Inc() => count++;
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var helper = new Helper();
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { helper.Inc(); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // A captured mutable STRUCT invoked through a non-readonly method mutates the hoisted
    // closure field on every re-execution -- covered by the mutating-value-receiver rule, the
    // lambda-capture analog of MZR004's boxed method-group receiver verdict.
    [Fact]
    public async Task OptimisticPatch_MutatingCapturedStructReceiver_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public struct Counter
            {
                private int count;

                public int Next(int x) => x + count++;
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var counter = new Counter();
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => counter.Next(x));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("captured local 'counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ExtensionMethodOnThis_MutatingEnclosingState_IsFlagged()
    {
        // `this.Inc()` runs the extension body with `this` bound to the receiver parameter:
        // the write through that parameter mutates the enclosing object exactly like
        // `this.Counter++` would, and the extension syntax must not hide it.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public static class CExtensions
            {
                public static void Inc(this C c) => c.Counter++;
            }

            public class C
            {
                public int Counter;

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { this.Inc(); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("field 'Counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ExtensionOnThis_AfterAnotherReceiver_IsStillFlagged()
    {
        // The earlier call on another object walks the same helper with no `this` binding:
        // it must not poison the chase for the later call that DOES hand it the enclosing
        // instance.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public static class CExtensions
            {
                public static void Inc(this C c) => c.Counter++;
            }

            public class C
            {
                public int Counter;

                public void M()
                {
                    var f = new MemoFactory();
                    var otherObject = new C();
                    f.CreateMemoizR(async () => { otherObject.Inc(); this.Inc(); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("field 'Counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task MutatingGetter_ReadInComputation_IsFlagged()
    {
        // Reading the property runs its getter on every evaluation: the hidden `counter++`
        // races exactly like the inline form.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private int counter;

                private int P { get { counter++; return 0; } }

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { return P; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("field 'counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task StaticHelperWithExplicitThisArgument_IsFlagged()
    {
        // `Mutate(this)` hands the enclosing instance to the parameter: the write through
        // it mutates the enclosing object exactly like `this.Counter++`.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public int Counter;

                private static void Mutate(C c) => c.Counter++;

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { Mutate(this); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("field 'Counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task OtherReceiverHelper_StaticWriteIsFlagged_MemberWriteIsNot()
    {
        // The helper runs on another object, but the STATIC it writes races regardless of
        // receiver; the write to that object's own member stays MZR001's territory.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static int hits;
                private int instanceCount;

                public void Touch()
                {
                    hits++;
                    instanceCount++;
                }

                public void M()
                {
                    var f = new MemoFactory();
                    var other = new C();
                    f.CreateMemoizR(async () => { other.Touch(); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("static field 'hits'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ForeignSetterHiddenStaticWrite_IsFlagged()
    {
        // The assignment target itself is a computation-local object, but the setter body
        // still mutates a static on every recomputation.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class Box
            {
                public static int hits;

                public int Value
                {
                    get => 0;
                    set { hits++; }
                }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () =>
                    {
                        var box = new Box();
                        box.Value = 1;
                        return 0;
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("static field 'hits'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ThisAliasInsideHelper_KeepsTheBinding()
    {
        // The helper stores the received instance in a local before handing it on: the
        // alias resolves to the this-bound parameter, so the nested write is still the
        // enclosing object's.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public int Counter;

                private static void Mutate(C c) => c.Counter++;

                private static void Wrapper(C c)
                {
                    var alias = c;
                    Mutate(alias);
                }

                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => { Wrapper(this); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("field 'Counter'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ExtensionMethodOnAnotherReceiver_IsNotChasedForMutation()
    {
        // The same extension on some OTHER object mutates that object's state -- a
        // captured-reference mutation that is deliberately MZR001's territory, not MZR002's.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public static class CExtensions
            {
                public static void Inc(this C c) => c.Counter++;
            }

            public class C
            {
                public int Counter;

                public void M()
                {
                    var f = new MemoFactory();
                    var otherObject = new C();
                    f.CreateMemoizR(async () => { otherObject.Inc(); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
    [Fact]
    public async Task AssembledPatchMutation_IsFlagged()
    {
        // The patch assembled by the out-helper replays on the state's flows exactly like an
        // inline patch: its write to the captured local is the same data race.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var applied = 0;
                    void Provide(out Func<int, int> d) => d = x => { applied++; return x; };
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch;
                        Provide(out patch);
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("captured local 'applied'", diagnostic.GetMessage());
    }
    [Fact]
    public async Task SurvivingRebindPatch_MutationIsFlagged()
    {
        // The declaration initializer is definitely overwritten before Apply: the surviving
        // write's lambda is the stored patch, and its captured-local write races on replay.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var applied = 0;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = static x => x;
                        patch = x => { applied++; return x; };
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR002", diagnostic.Id);
        Assert.Contains("captured local 'applied'", diagnostic.GetMessage());
    }
}
