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

    // nameof(shared) is a compile-time string: the built delegate neither captures nor reads
    // the symbol, so nothing crosses flows.
    [Fact]
    public async Task NameofOperands_AreNotCaptures()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var shared = new List<int>();
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + nameof(shared).Length + nameof(hits).Length);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // A straight-line reassignment AFTER the call cannot change the delegate the overlay
    // already stored; only assignments that can execute before it distrust the initializer.
    [Fact]
    public async Task ReassignmentAfterTheCall_KeepsTheInitializerTrusted()
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
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = static x => x;
                        await ctx.Apply(state, patch);
                        patch = x => x + shared.Count;
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task LoopCarriedReassignment_IsStillUnresolvable()
    {
        // Textually after the call, but the loop carries the reassigned delegate back into
        // the next iteration's Apply.
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
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = static x => x;
                        for (var i = 0; i < 2; i++)
                        {
                            await ctx.Apply(state, patch);
                            patch = x => x + shared.Count;
                        }
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
    public async Task LoopLocalDelegate_TrailingReassignment_IsNotCarried()
    {
        // The delegate local is declared INSIDE the loop body: freshly initialized each
        // iteration, so the trailing reassignment dies with its iteration and can never
        // reach a call -- the initializer stays trustworthy.
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
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        for (var i = 0; i < 2; i++)
                        {
                            Func<int, int> patch = static x => x;
                            await ctx.Apply(state, patch);
                            patch = x => x + shared.Count;
                        }
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DeconstructionReassignment_IsUnresolvable()
    {
        // `(patch, _) = ...` writes `patch` just as much as `patch = ...`: the tuple
        // left-hand side must be flattened before comparing symbols.
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
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = static x => x;
                        (patch, _) = ((Func<int, int>)(x => x + shared.Count), 0);
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

    // A static read inside a callback the patch merely BUILDS runs later, off the overlay's
    // re-execution, on whatever flow invokes it -- the same deferred-callback shape MZR003
    // prunes. (Closure captures keep the full walk: a built callback still pins them.)
    [Fact]
    public async Task StaticReadInACallbackThePatchOnlyBuilds_IsNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> later = () => hits;
                            _ = later;
                            return x;
                        });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // A method group the patch stores builds a delegate without running it -- the same
    // deferred shape as a built lambda, so its statics must not count as patch reads.
    [Fact]
    public async Task MethodGroupStoredInACallback_IsNotExecutedForStatics()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                private static int ReadHits() => hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> later = ReadHits;
                            _ = later;
                            return x;
                        });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // An `in` argument hands out a readonly reference: it cannot rebind the local, so it must
    // not distrust the initializer.
    [Fact]
    public async Task InArgument_DoesNotDistrustTheInitializer()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static void Use(in Func<int, int> candidate) { }

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = static x => x;
                        Use(in patch);
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
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

    // The closure hoists the VARIABLE, not a copy: a mutable struct that is fine as a
    // (copied) node value is writable shared storage as a capture.
    [Fact]
    public async Task CapturedMutableStruct_IsFlagged_ReadonlyStructIsNot()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public struct Counter
            {
                public int Value;
            }

            public readonly struct Snapshot
            {
                public readonly int Value;

                public Snapshot(int value) { Value = value; }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var counter = new Counter();
                    var snap = new Snapshot(1);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + counter.Value + snap.Value);
                    });
                    counter.Value++;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'counter'", diagnostic.GetMessage());
        Assert.Contains("mutable struct", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ForInitializerDelegate_CarriesItsReassignment()
    {
        // Declared in the for-INITIALIZER, the variable outlives each iteration: the trailing
        // reassignment is what the next iteration's Apply stores.
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
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        for (Func<int, int> patch = static x => x; ; )
                        {
                            await ctx.Apply(state, patch);
                            patch = x => x + shared.Count;
                        }
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'patch'", diagnostic.GetMessage());
        Assert.Contains("reassigned", diagnostic.GetMessage());
    }

    // A property read runs its getter exactly like a call: `static int Hits => hits;` is the
    // helper-method evasion with property syntax.
    [Fact]
    public async Task GetOnlyStaticProperty_HidingAStaticRead_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static int hits;

                private static int Hits => hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Hits);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    // Top-level statements put captured locals in a compiler-synthesized entry point with no
    // declaration of its own -- the enclosing-function test must not lose them.
    [Fact]
    public async Task TopLevelStatementCaptures_AreShared()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            var f = new MemoFactory();
            var state = f.CreateOptimistic<int>(f.CreateSignal(1));
            var shared = new List<int>();
            f.CreateAction<int>(async (p, ctx) =>
            {
                await ctx.Apply(state, x => x + shared.Count);
            });
            """, new OptimisticPatchCaptureAnalyzer(), OutputKind.ConsoleApplication);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("shared", diagnostic.GetMessage());
    }

    [Fact]
    public async Task NameofProperty_DoesNotChaseItsGetter()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static int hits;

                private static int Hits => hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + nameof(Hits).Length);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NameofMention_DoesNotSuppressTheRealRead()
    {
        // The nameof mention comes first in the walk; it must not enter the visited set and
        // swallow the chase of the real read that follows.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static int hits;

                private static int Hits => hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + nameof(Hits).Length + Hits);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
    }

    // `alias = ...` rebinds patch just as directly as `patch = ...`.
    [Fact]
    public async Task RefAliasReassignment_IsUnresolvable()
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
                    ref var alias = ref patch;
                    alias = x => x + shared.Count;
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

    // A constructor and a user-defined operator run on every replay exactly like helper calls.
    [Fact]
    public async Task ConstructorAndOperator_HidingStaticReads_AreChased()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class Meter
            {
                private static int hits;

                public readonly int Sample;

                public Meter() { Sample = hits; }

                public static int operator +(Meter meter, int x) => x + hits;
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => new Meter() + x);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    // Every link of the alias chain gets the reassignment check, against the site where its
    // value is READ: patch is never written again, but it copied p0 after p0's reassignment.
    [Fact]
    public async Task ReassignedAlias_IsUnresolvable()
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
                    Func<int, int> p0 = static x => x;
                    p0 = x => x + shared.Count;
                    Func<int, int> patch = p0;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("'p0'", diagnostic.GetMessage());
        Assert.Contains("reassigned", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AliasReassignedAfterTheCopy_KeepsTheInitializerTrusted()
    {
        // p0's reassignment happens AFTER patch copied it: the copied value is the harmless
        // initializer, so nothing distrusts the chain.
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
                    Func<int, int> p0 = static x => x;
                    Func<int, int> patch = p0;
                    p0 = x => x + shared.Count;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // An event's backing delegate is writable storage by construction: subscribers on other
    // flows mutate what the patch reads.
    [Fact]
    public async Task StaticEventRead_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static event Action? Changed;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { Changed?.Invoke(); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("Changed", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    // A delegate the patch builds AND invokes runs on every replay -- only the
    // built-but-deferred shape is pruned.
    [Fact]
    public async Task ImmediatelyInvokedBuiltDelegate_IsChasedForStatics()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> later = () => hits;
                            return x + later();
                        });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AssignmentBuiltDelegate_Invoked_IsChasedForStatics()
    {
        // No declaration initializer: the delegate is assembled by assignment, so every
        // same-tree assignment's right-hand side might be the invoked body.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> later;
                            later = () => hits;
                            return x + later();
                        });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DeconstructionBuiltDelegate_Invoked_IsChasedForStatics()
    {
        // `(later, _) = ...` assembles the delegate just like `later = ...`: the tuple's
        // elements are the bodies that might be invoked.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> later;
                            (later, _) = ((Func<int>)(() => hits), 0);
                            return x + later();
                        });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AssignmentAfterTheOnlyInvoke_IsNotChased()
    {
        // The second assignment executes after the only invocation: its callback is built but
        // never run during the replay.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> later;
                            later = static () => 0;
                            var y = later();
                            later = () => hits;
                            _ = later;
                            return x + y;
                        });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DeconstructionElement_AssignedToAnotherVariable_IsNotChased()
    {
        // Positional pairing: the hits callback lands in `other`, which is never invoked.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> other;
                            Func<int> later;
                            (other, later) = ((Func<int>)(() => hits), static () => 0);
                            _ = other;
                            return x + later();
                        });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InstanceFieldPatch_WrittenOnAFreshOtherInstance_StaysTrusted()
    {
        // `other.patch = ...` on a freshly constructed instance cannot rebind `this.patch`.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private Func<int, int> patch = static x => x;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var shared = new List<int>();
                    var other = new C();
                    other.patch = x => x + shared.Count;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ComputedStaticGetter_ReturningFreshValues_IsNotFlagged_AutoPropertyIs()
    {
        // `Items` allocates per replay -- nothing shared; `Stored` is backing storage holding
        // ONE List shared by every flow.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private static List<int> Items => new();

                private static List<int> Stored { get; } = new();

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Items.Count + Stored.Count);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("Stored", diagnostic.GetMessage());
        Assert.Contains("not Sendable", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DeconstructionDeclaredDelegate_ResolvesItsInitializer()
    {
        // `var (patch, _) = (...)` declares through a designation: the safe lambda is right
        // there in the tuple, so the delegate must not be reported as unverifiable.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    var (patch, _) = ((Func<int, int>)(static x => x), 0);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NullConditionalInvoke_IsChasedForStatics()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int>? later = () => hits;
                            return x + (later?.Invoke() ?? 0);
                        });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    // An assignment target runs the SETTER: a hidden static read in one replays with the
    // patch just like a getter's.
    [Fact]
    public async Task StaticReadHiddenInAPropertySetter_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            [Sendable]
            public class C
            {
                private static int hits;

                private int P { get => 0; set { _ = hits; } }

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { P = 1; return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    [Fact]
    public async Task FirstAssignedDelegate_ResolvesLikeAnInitializer()
    {
        // Declaration in two steps: the sole assignment IS the initialization, not a rebind.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    Func<int, int> patch;
                    patch = static x => x;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InvokeAfterReassignment_ChasesTheNewBody()
    {
        // The second call executes the reassigned closure: the per-site guard must not let
        // the first (safe) invocation swallow it.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> later = static () => 0;
                            _ = later();
                            later = () => hits;
                            return x + later();
                        });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    // A computed get-only property is a helper call in disguise: its getter re-reads the
    // mutable field on every replay.
    [Fact]
    public async Task ComputedInstanceGetter_ReadingMutableField_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private int counter;

                private int Counter => counter;

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
        Assert.Contains("counter", diagnostic.GetMessage());
        Assert.Contains("writable state", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RefAliasAssignedDelegate_Invoked_IsChasedForStatics()
    {
        // The assignment goes through a ref alias; it rebinds `later` all the same.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static int hits;

                public void M()
                {
                    var f = new MemoFactory();
                    var state = f.CreateOptimistic<int>(f.CreateSignal(1));
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x =>
                        {
                            Func<int> later = static () => 0;
                            ref var alias = ref later;
                            alias = () => hits;
                            return x + later();
                        });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR004", diagnostic.Id);
        Assert.Contains("hits", diagnostic.GetMessage());
        Assert.Contains("writable static state", diagnostic.GetMessage());
    }

    [Fact]
    public async Task GetterLocalCallback_NeverInvoked_IsNotFlagged()
    {
        // The getter allocates and discards the callback on each replay: nothing of it is
        // stored or executed, so its captures must not count.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private int counter;

                private int Counter
                {
                    get
                    {
                        Func<int> later = () => counter;
                        _ = later;
                        return 0;
                    }
                }

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

        Assert.Empty(diagnostics);
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
