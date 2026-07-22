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
    public async Task EnclosingProperties_SettableIsFlagged_GetOnlyAndInitOnlySendableAreNot()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public int Threshold { get; set; }   // settable: flagged
                public int Limit { get; init; }      // init-only is immutable state: held to its (Sendable) type
                public string Tag => "t";            // computed get-only, Sendable type: fine

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Threshold + Limit + Tag.Length);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("Threshold", diagnostic.GetMessage());
        Assert.Contains("writable state", diagnostic.GetMessage());
    }

    // A method-group patch captures its RECEIVER into the stored delegate; the receiver is
    // shared across pull flows even when the method body cannot be walked.
    [Fact]
    public async Task MethodGroupPatch_MutableReceiver_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class Helper
            {
                public int Count;

                public int Patch(int x) => x + Count;
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
                        await ctx.Apply(state, helper.Patch);
                    });
                }
            }
            """);

        Assert.All(diagnostics, d => Assert.Equal("MZR004", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'helper'") && d.GetMessage().Contains("not Sendable"));
    }

    [Fact]
    public async Task MethodGroupPatch_StoredInADelegateVariable_FlagsTheReceiver()
    {
        // The receiver hides behind a variable: `Apply` sees only a local reference, so the
        // method group must be resolved through the (same-tree) initializer.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class Helper
            {
                public int Count;

                public int Patch(int x) => x + Count;
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var helper = new Helper();
                    Func<int, int> patch = helper.Patch;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.All(diagnostics, d => Assert.Equal("MZR004", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'helper'") && d.GetMessage().Contains("not Sendable"));
    }

    [Fact]
    public async Task MethodGroupPatch_ImmutableOrStaticReceiver_IsNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public sealed record Calm(int Bias)
            {
                public int Patch(int x) => x + Bias;
            }

            public class C
            {
                private static int StaticPatch(int x) => x;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var calm = new Calm(1);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, calm.Patch);
                        await ctx.Apply(state, StaticPatch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // Hiding the state read behind an instance helper still captures `this`; the enclosing
    // OBJECT is what crosses flows, so it is held to its type's sendability.
    [Fact]
    public async Task HelperCallHidingTheRead_FlagsTheCapturedThis()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private int counter;

                private int ReadCounter() => counter;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        // Two helper calls, one capture: `this` is deduplicated like any symbol.
                        await ctx.Apply(state, x => x + ReadCounter() + ReadCounter());
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'this'", diagnostic.GetMessage());
        Assert.Contains("not Sendable", diagnostic.GetMessage());
    }

    [Fact]
    public async Task HelperCallOnASendableEnclosingType_IsNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private readonly int bias;

                private int Bias() => bias;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Bias());
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // Static state is shared across every flow without any capture: the read baked into the
    // stored delegate races with whoever mutates the static.
    [Fact]
    public async Task StaticState_WritableOrNonSendable_IsFlagged_ConstAndImmutableAreNot()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private static int hits;                          // writable static: flagged
                private static readonly List<int> Cache = new();  // readonly, but the List is shared: flagged
                public static int Threshold { get; set; }         // settable static property: flagged
                private const int Max = 5;                        // compile-time constant: fine
                private static readonly string Name = "n";        // readonly + Sendable: fine
                private static string Label => "l";               // get-only, Sendable: fine

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + hits + Cache.Count + Threshold + Max + Name.Length + Label.Length);
                    });
                }
            }
            """);

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR004", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("hits") && d.GetMessage().Contains("writable static state"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("Cache") && d.GetMessage().Contains("not Sendable"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("Threshold") && d.GetMessage().Contains("writable static state"));
    }

    // [Sendable] is the trust escape hatch (the type asserts internal synchronization); the
    // classifier vets the whole object, and re-walking its members would override that trust.
    [Fact]
    public async Task SendableAttributedReceiver_MethodGroupBody_IsNotReWalked()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            [Sendable]
            public class Helper
            {
                public int Count;

                public int Patch(int x) => x + Count;
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
                        await ctx.Apply(state, helper.Patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SendableAttributedEnclosingType_DirectReads_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            [Sendable]
            public class C
            {
                private int counter;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + counter);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // The classifier deliberately ignores statics (they are not part of instance transfer), so
    // a Sendable `this` must not silence a static read hidden behind helpers: same-tree helper
    // bodies are chased, transitively.
    [Fact]
    public async Task StaticReadHiddenBehindHelpers_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static int hits;

                private int ReadHits() => hits;

                private int Indirect() => ReadHits();

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Indirect());
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    // A struct receiver is Sendable by copy semantics, but a method-group delegate stores ONE
    // boxed copy that every re-execution shares -- a non-readonly method mutates that box.
    [Fact]
    public async Task MutableStructReceiver_NonReadonlyMethodGroup_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public struct Counter
            {
                private int count;

                public int Patch(int x) => x + count++;
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
                        await ctx.Apply(state, counter.Patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'counter'", diagnostic.GetMessage());
        Assert.Contains("boxed receiver", diagnostic.GetMessage());
    }

    [Fact]
    public async Task StructReceiver_ReadonlyMethodGroup_IsNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public struct Counter
            {
                private int count;

                public readonly int Peek(int x) => x + count;
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
                        await ctx.Apply(state, counter.Peek);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // An already-built delegate flowing in as data is the one shape Roslyn cannot see into --
    // and this rule is the only check a patch ever gets, so unverifiable means flagged.
    [Fact]
    public async Task PrebuiltDelegateParameter_IsFlaggedAsUnverifiable()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M(Func<int, int> patch)
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'patch'", diagnostic.GetMessage());
        Assert.Contains("cannot be resolved", diagnostic.GetMessage());
    }

    // A local function has no receiver, so no receiver/this verdict covers its closure: moving
    // the read behind one declared outside the patch must not evade the rule.
    [Fact]
    public async Task LocalFunctionHelper_CapturingNonSendableState_IsFlagged()
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
                    int Count() => shared.Count;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Count());
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("shared", diagnostic.GetMessage());
        Assert.Contains("not Sendable", diagnostic.GetMessage());
    }

    [Fact]
    public async Task HelperLocalClosures_AreNotPatchCaptures()
    {
        // Inner captures tmp -- but tmp belongs to Count's INVOCATION, recreated on every
        // patch execution: nothing of it is stored in the optimistic delegate.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    int Count()
                    {
                        var tmp = new List<int>();
                        int Inner() => tmp.Count;
                        return Inner();
                    }
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Count());
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // The initializer proves nothing once the variable is reassigned: the overlay may store
    // the second closure, so the variable is held to the unresolvable-delegate verdict.
    [Fact]
    public async Task ReassignedDelegateVariable_IsFlaggedAsUnverifiable()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var shared = new List<int>();
                    Func<int, int> patch = static x => x;
                    patch = x => x + shared.Count;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'patch'", diagnostic.GetMessage());
        Assert.Contains("reassigned", diagnostic.GetMessage());
    }

    [Fact]
    public async Task PatchInternalLocalFunction_UsingPatchLocals_IsNotFlagged()
    {
        // The local function lives INSIDE the patch: its reads of patch-locals are
        // patch-internal state created fresh per execution, not cross-flow sharing.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            var list = new List<int> { x };
                            int Count() => list.Count;
                            return x + Count();
                        });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // No setter, yet `ref int Counter => ref counter` hands out assignable live storage.
    [Fact]
    public async Task RefReturningProperty_IsFlaggedAsWritable()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private int counter;

                public ref int Counter => ref counter;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Counter);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("Counter", diagnostic.GetMessage());
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
