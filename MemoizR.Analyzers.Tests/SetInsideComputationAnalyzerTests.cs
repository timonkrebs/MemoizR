using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR003: Set inside a memo / reaction / map-reduce computation (same-flow
// evaluation hosts, where the runtime deterministically throws) is flagged; Set outside any
// computation and Set inside ConcurrentMap children (forced fresh scopes) are not.
public class SetInsideComputationAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new SetInsideComputationAnalyzer());

    [Fact]
    public async Task SetInsideMemoComputation_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.CreateMemoizR(async () => { await v.Set(2); return 1; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    // Invalidate (the ADR 0007 refresh) takes the same exclusive lock as Set and throws
    // identically inside a same-flow computation; the runtime contract is pinned by
    // StabilizationAndInvalidateTests.Invalidate_InsideComputation_IsRejectedLikeSet.
    [Fact]
    public async Task InvalidateInsideMemoComputation_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var m1 = f.CreateMemoizR(async () => 1);
                    f.CreateMemoizR(async () =>
                    {
                        await m1.Invalidate();
                        return 2;
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains(".Invalidate", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideReaction_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.BuildReaction().CreateAdvancedReaction(async () => await v.Set(2));
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task SetInsideConcurrentMapReduceChild_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.CreateConcurrentMapReduce<int>((a, b) => a, async _ => { await v.Set(2); return 1; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task SetInsideActorMemoComputation_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateActorSignal(1);
                    f.CreateActorMemoizR(async () => { await v.Set(2); return 1; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("ActorSignal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideConcurrentRaceResolver_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.CreateConcurrentRace<int, int>(
                        async () => { await v.Set(2); return 0; }, // resolver runs on the parent flow
                        async (g, r) => 1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task ProvablyCrossFactorySet_IsNotFlagged()
    {
        // The Set locks the TARGET signal's own context, where f1's computation holds nothing:
        // no exclusive-inside-upgradeable conflict, no runtime throw. Skipped only because both
        // factories resolve and differ -- an unprovable target keeps the diagnostic.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    f1.CreateMemoizR(async () => { await other.Set(2); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SameKeyFactoriesSet_IsStillFlagged()
    {
        // Two factory VARIABLES constructed with the same key share one context -- and its
        // lock -- so the Set throws at runtime exactly like the same-factory case: variable
        // inequality alone must not suppress the diagnostic.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory("shared");
                    var f2 = new MemoFactory("shared");
                    var s = f2.CreateSignal(0);
                    f1.CreateMemoizR(async () => { await s.Set(1); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task BlankKeyFactoriesSet_IsNotFlagged()
    {
        // The runtime treats whitespace keys as UNKEYED (each factory owns a fresh context), so
        // two new MemoFactory("") instances do NOT share a lock and the Set does not throw:
        // the raw blank strings must not count as one shared key.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory("");
                    var f2 = new MemoFactory("");
                    var s = f2.CreateSignal(0);
                    f1.CreateMemoizR(async () => { await s.Set(1); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InlineCrossFactorySet_IsNotFlagged()
    {
        // The Set target is created INLINE by a different unkeyed factory: its provenance is
        // the creation invocation itself, no variable initializer needed.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    f1.CreateMemoizR(async () => { await f2.CreateSignal(0).Set(1); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task PropertyHeldComputationDelegate_IsAnalyzed()
    {
        // The computation reaches the factory through an auto-PROPERTY initializer: same lambda
        // as the inline/local/field forms, same diagnosis. (Property initializers cannot touch
        // instance state, hence the statics.)
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                private static readonly MemoFactory F = new();
                private static readonly Signal<int> S = F.CreateSignal(1);

                public static Func<Task<int>> Compute { get; } = async () => { await S.Set(2); return 0; };

                public void M()
                {
                    F.CreateMemoizR(Compute);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task PropertyHeldCrossContextSet_IsNotFlagged()
    {
        // Host factory and Set target live in auto-properties: their initializers resolve like
        // variable initializers, so the provably-different-context suppression applies.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static MemoFactory F1 { get; } = new MemoFactory();
                private static MemoFactory F2 { get; } = new MemoFactory();
                private static Signal<int> S { get; } = F2.CreateSignal(0);

                public void M()
                {
                    F1.CreateMemoizR(async () => { await S.Set(1); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ShadowedSignalLookalikeSet_IsNotFlagged()
    {
        // A source-shadowed MemoizR.Signal<T> lookalike's Set takes no evaluation lock and does
        // not throw; name matching alone must not claim a deterministic runtime failure.
        var diagnostics = await AnalyzeAsync("""
            using System.Threading.Tasks;
            using MemoizR;

            namespace MemoizR
            {
                public class Signal<T>
                {
                    public Task Set(T value) => Task.CompletedTask;
                }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var fake = new MemoizR.Signal<int>();
                    f.CreateMemoizR(async () => { await fake.Set(1); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CrossEngineSet_IsNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var lockSig = f.CreateSignal(1);
                    var actorSig = f.CreateActorSignal(1);
                    // lock-engine Signal.Set inside an actor computation: no same-flow lock, does not throw
                    f.CreateActorMemoizR(async () => { await lockSig.Set(2); return 1; });
                    // ActorSignal.Set inside a lock-engine computation: does not throw either
                    f.CreateMemoizR(async () => { await actorSig.Set(2); return 1; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SetInsideLocalFunctionComputation_IsFlagged()
    {
        // The computation is a local function passed as a method group: its body must be
        // resolved and analyzed exactly like a lambda's.
        var diagnostics = await AnalyzeAsync("""
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.CreateMemoizR(Compute);

                    async Task<int> Compute() { await v.Set(2); return 1; }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideComputation_PassedThroughADelegateVariable_IsFlagged()
    {
        // The computation reaches the factory through a local: the variable's initializer is
        // resolved to the same lambda body the inline form diagnoses.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    Func<Task<int>> compute = async () => { await v.Set(2); return 0; };
                    f.CreateMemoizR(compute);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task SetInsideDeferredCallback_BuiltByTheComputation_IsNotFlagged()
    {
        // The diagnostic's own fix guidance: "schedule the write outside the evaluation". A
        // callback the computation BUILDS runs later on a flow that holds no evaluation lock,
        // so neither a stored lambda nor a local-function declaration may be flagged; the
        // direct Set in the second memo must still be.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                private Func<Task>? deferred;

                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.CreateMemoizR<int>(async () =>
                    {
                        deferred = async () => await v.Set(2); // deferred: runs outside the evaluation
                        Task Later() => v.Set(3); // declared, not executed here
                        return 1;
                    });
                    f.CreateMemoizR(async () => { await v.Set(4); return 1; }); // direct: still flagged
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task SetOutsideComputations_AndInsideConcurrentMapChildren_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public async Task M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    await v.Set(2); // ordinary write outside any evaluation

                    // ConcurrentMap children run on forced fresh scopes, not inside their
                    // parent's upgradeable lock; deliberately not flagged.
                    f.CreateConcurrentMap<int>(async _ => { await v.Set(3); return 1; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // A patch runs inside the view memo's recompute, whose flow holds the evaluation lock in
    // upgradeable mode -- a Set in one throws exactly like a Set in the memo's own computation.
    [Fact]
    public async Task SetInsideOptimisticPatch_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = v.Set(2); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task HelperReturnedState_DoesNotProveTheHostFactory()
    {
        // MakeState merely RETURNS a state: its receiver (f1) says nothing about which factory
        // created it -- here it is really F2's, whose context the patch evaluates under, so the
        // Set on F2's signal throws at runtime and the suppression must not trust the helper.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;
            using MemoizR.Reactive;

            public static class StateHelpers
            {
                public static readonly MemoFactory F2 = new MemoFactory();

                public static OptimisticState<int> MakeState(this MemoFactory f)
                    => F2.CreateOptimistic<int>(F2.CreateSignal(1));
            }

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var other = StateHelpers.F2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(f1.MakeState(), x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    // A helper the computation CALLS executes its Set under the same evaluation lock: hiding
    // the write behind a local function must not evade the diagnostic.
    [Fact]
    public async Task SetHiddenInALocalHelper_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    void Write() { _ = v.Set(2); }
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { Write(); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    // A constructor the patch executes Sets under the same evaluation lock: non-invocation
    // syntax (new, getters, operators) must not evade the chase.
    [Fact]
    public async Task SetHiddenInAConstructor_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class Writer
            {
                public Writer(Signal<int> target) { _ = target.Set(2); }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = new Writer(v); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    // A property assignment executes the SETTER: a Set inside one throws under the same
    // evaluation lock as an inline Set.
    [Fact]
    public async Task SetHiddenInAPropertySetter_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static readonly MemoFactory F = new();
                private static readonly Signal<int> V = F.CreateSignal(1);

                private static int P { get => 0; set { _ = V.Set(2); } }

                public void M()
                {
                    F.CreateMemoizR(async () => { P = 1; return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DeconstructionDeclaredState_ProvesTheHostFactory()
    {
        // `var (state, _) = (f1.CreateOptimistic(...), 0)` declares through a designation: the
        // creation is right there in the tuple, so cross-factory suppression must still apply.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var (state, _) = (f1.CreateOptimistic<int>(f1.CreateSignal(1)), 0);
                    var other = f2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FirstAssignedState_ProvesTheHostFactory()
    {
        // Declaration in two steps: the sole assignment is the initialization, so the
        // cross-factory suppression still applies.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;
            using MemoizR.Reactive;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    OptimisticState<int> state;
                    state = f1.CreateOptimistic<int>(f1.CreateSignal(1));
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task HelperArgumentProvenance_IsSubstituted()
    {
        // The chased Set's target is the helper's parameter: the call-site argument `other`
        // (a provably disjoint factory's signal) is what actually gets Set, exactly as the
        // inline form -- the suppression must survive the helper boundary.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    void Write(Signal<int> s) { _ = s.Set(2); }
                    f1.CreateMemoizR(async () => { Write(other); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SignalParameterAliasedInsideHelper_ResolvesThroughTheCallSite()
    {
        // The helper stores its parameter in a local before the Set: the alias resolves to
        // the parameter and the parameter to the call-site argument, so the cross-factory
        // suppression survives -- while the same helper called with the HOST factory's own
        // signal is still flagged (per-call-site provenance, not per-helper).
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var mine = f1.CreateSignal(1);
                    void Write(Signal<int> s)
                    {
                        var a = s;
                        _ = a.Set(2);
                    }
                    f1.CreateMemoizR(async () => { Write(other); return 0; });
                    f1.CreateMemoizR(async () => { Write(mine); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ExtensionHelperReceiver_KeepsCallSiteProvenance()
    {
        // The Set target is the extension helper's `this` parameter: the receiver at the
        // call site (a provably disjoint factory's signal) is what actually gets Set, while
        // the same helper on the HOST factory's own signal is still flagged.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public static class SignalExtensions
            {
                public static void Write(this Signal<int> s)
                {
                    _ = s.Set(2);
                }
            }

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var mine = f1.CreateSignal(1);
                    f1.CreateMemoizR(async () => { other.Write(); return 0; });
                    f1.CreateMemoizR(async () => { mine.Write(); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SignalParameterRebound_ResolvesTheAssignedFactory()
    {
        // The helper unconditionally rebinds its parameter before the Set: the target is
        // provably f2's signal regardless of what the caller passed.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var mine = f1.CreateSignal(1);
                    void Write(Signal<int> s)
                    {
                        s = f2.CreateSignal(0);
                        _ = s.Set(2);
                    }
                    f1.CreateMemoizR(async () => { Write(mine); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FactoryParameterProvenance_IsSubstituted()
    {
        // The helper CREATES the target through its factory parameter: the call-site factory
        // argument is the provenance, so the disjoint-factory call is suppressed while the
        // host factory's own call is still flagged.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    void Write(MemoFactory f)
                    {
                        var s = f.CreateSignal(0);
                        _ = s.Set(1);
                    }
                    f1.CreateMemoizR(async () => { Write(f2); return 0; });
                    f1.CreateMemoizR(async () => { Write(f1); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideAnOutAssembledPatch_IsFlagged()
    {
        // The patch assembled by the out-helper runs inside the view's evaluation lock: its
        // Set throws exactly like an inline patch's.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    void Provide(out Func<int, int> d) => d = x => { _ = v.Set(2); return x; };
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
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideAnOutAssembledPatch_KeepsCallSiteProvenance()
    {
        // The assembled patch writes through the helper's signal parameter: the call-site
        // argument decides -- a disjoint factory's signal is suppressed, the host factory's
        // own is flagged.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var mine = f1.CreateSignal(1);
                    var state = f1.CreateOptimistic<int>(mine);
                    void Provide(Signal<int> s, out Func<int, int> p) => p = x => { _ = s.Set(1); return x; };
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch;
                        Provide(other, out patch);
                        await ctx.Apply(state, patch);
                    });
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch;
                        Provide(mine, out patch);
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideAnIndexerPatch_KeepsCallSiteProvenance()
    {
        // The indexer's returned lambda writes through the index parameter: the call-site
        // signal decides, exactly like a helper argument.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private Func<int, int> this[Signal<int> s]
                {
                    get
                    {
                        return x => { _ = s.Set(1); return x; };
                    }
                }

                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var mine = f1.CreateSignal(1);
                    var state = f1.CreateOptimistic<int>(mine);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, this[other]);
                    });
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, this[mine]);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideAForwardedDelegateParameter_IsFlagged()
    {
        // The out-helper hands back its OTHER delegate parameter: the call-site lambda with
        // the Set is what the overlay stores.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    void Provide(Func<int, int> source, out Func<int, int> p) => p = source;
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch;
                        Provide(x => { _ = v.Set(2); return x; }, out patch);
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideAFactoryReturnedPatch_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var mine = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(mine);
                    Func<int, int> Make(Signal<int> s) => x => { _ = s.Set(1); return x; };
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, Make(mine));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task OverwrittenOutHelperPatchBody_IsNotWalked()
    {
        // The helper's stale Set-lambda is definitely overwritten before it returns: only
        // the safe second delegate can be the stored patch.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    void Provide(out Func<int, int> p)
                    {
                        p = x => { _ = v.Set(2); return x; };
                        p = static x => x;
                    }
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch;
                        Provide(out patch);
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SetInsideAComputedPropertyPatch_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private readonly MemoFactory f = new();
                private readonly Signal<int> v;

                private Func<int, int> Patch
                {
                    get
                    {
                        return x => { _ = v.Set(2); return x; };
                    }
                }

                public C()
                {
                    v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, Patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideAConditionalComputationArm_IsFlagged()
    {
        // The conditional picks one of two computations at build time: either arm can be
        // the body the factory stores, so a Set inside one must not hide behind the ternary.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M(bool choose)
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.CreateMemoizR(choose
                        ? (Func<Task<int>>)(async () => { _ = v.Set(2); return 1; })
                        : async () => 2);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task FactoryAliasInsideHelper_KeepsCallSiteProvenance()
    {
        // The helper aliases its factory parameter before creating the signal: the
        // call-site factory is still the provenance, so the disjoint call is suppressed
        // while the host factory's own call stays flagged.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    void Write(MemoFactory f)
                    {
                        var host = f;
                        var s = host.CreateSignal(0);
                        _ = s.Set(1);
                    }
                    f1.CreateMemoizR(async () => { Write(f2); return 0; });
                    f1.CreateMemoizR(async () => { Write(f1); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task IndexerSetterArguments_KeepCallSiteProvenance()
    {
        // The Set target is the indexer's index parameter: the call-site index argument (a
        // provably disjoint factory's signal) is what actually gets Set, while the same
        // indexer fed the HOST factory's own signal is still flagged.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class Wrapper
            {
                public int this[Signal<int> s]
                {
                    set { _ = s.Set(value); }
                }
            }

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var mine = f1.CreateSignal(1);
                    var wrapper = new Wrapper();
                    f1.CreateMemoizR(async () => { wrapper[other] = 1; return 0; });
                    f1.CreateMemoizR(async () => { wrapper[mine] = 1; return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ZeroArgumentLocalFunction_KeepsTheArgumentMap()
    {
        // The helper's local function closes over the helper's signal parameter: the
        // call-site provenance must survive the zero-argument inner call.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    void Write(Signal<int> s)
                    {
                        void Inner() { _ = s.Set(2); }
                        Inner();
                    }
                    f1.CreateMemoizR(async () => { Write(other); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    // `using` runs Dispose before the evaluation exits, under the same lock.
    [Fact]
    public async Task SetHiddenInDispose_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class Writer : IDisposable
            {
                private readonly Signal<int> target;

                public Writer(Signal<int> target) { this.target = target; }

                public void Dispose() { _ = target.Set(2); }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.CreateMemoizR(async () =>
                    {
                        using var w = new Writer(v);
                        return 0;
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InterfaceDeclaredUsingResource_ChasesTheConcreteDispose()
    {
        // The initializer's type beats the declared interface: Writer.Dispose is what runs.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class Writer : IDisposable
            {
                private readonly Signal<int> target;

                public Writer(Signal<int> target) { this.target = target; }

                public void Dispose() { _ = target.Set(2); }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    f.CreateMemoizR(async () =>
                    {
                        using IDisposable w = new Writer(v);
                        return 0;
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task SetterValueParameter_SubstitutesTheAssignedSignal()
    {
        // `P = other` runs the setter with value = other, a provably disjoint factory's
        // signal: the suppression must survive the accessor boundary.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                private static Signal<int> P { get => null!; set { _ = value.Set(2); } }

                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    f1.CreateMemoizR(async () => { P = other; return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task HelperRewalkedPerCallSite_CatchesTheSameFactoryCall()
    {
        // The first (suppressed, cross-factory) call must not cache away the second call,
        // whose same-factory Set throws.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var v = f1.CreateSignal(1);
                    void Write(Signal<int> s) { _ = s.Set(2); }
                    f1.CreateMemoizR(async () => { Write(other); Write(v); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task LiftedRebind_InvokedOnlyAfterTheCreation_KeepsTheSuppression()
    {
        // The method-group lift is not execution: the delegate's only invocation runs after
        // the creation already used the initializer's factory.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var host = f1;
                    void Rebind() { host = f2; }
                    Action later = Rebind;
                    var other = f2.CreateSignal(1);
                    host.CreateMemoizR(async () => { await other.Set(2); return 0; });
                    later();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task RebindCalledOnlyAfterTheCreation_KeepsTheSuppression()
    {
        // Rebind's only call site runs after the creation already used the initializer's
        // factory: the write cannot precede the read.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var host = f1;
                    void Rebind() { host = f2; }
                    var other = f2.CreateSignal(1);
                    host.CreateMemoizR(async () => { await other.Set(2); return 0; });
                    Rebind();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DeadLocalFunctionRebind_KeepsTheSuppression()
    {
        // Rebind is declared but never referenced: dead code cannot execute, so the factory
        // alias's initializer still proves provenance. The moment it IS invoked, execution
        // order becomes unknowable and the diagnostic returns.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var host = f1;
                    void Rebind() { host = f2; }
                    var other = f2.CreateSignal(1);
                    host.CreateMemoizR(async () => { await other.Set(2); return 0; });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InvokedLocalFunctionRebind_KeepsTheDiagnostic()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var host = f1;
                    void Rebind() { host = f2; }
                    Rebind();
                    var other = f2.CreateSignal(1);
                    host.CreateMemoizR(async () => { await other.Set(2); return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task ReassignedFactoryAlias_KeepsTheDiagnostic()
    {
        // `host` holds f2 at the creation, not its f1 initializer: the stale alias must not
        // prove cross-factory disjointness for a Set that throws on f2's context.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var host = f1;
                    host = f2;
                    var v = f2.CreateSignal(0);
                    var state = host.CreateOptimistic<int>(v);
                    f2.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = v.Set(1); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task RefAliasReboundState_KeepsTheDiagnostic()
    {
        // The state is rebound through a ref alias to another factory's view: the stale
        // initializer must not prove provenance.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var state = f1.CreateOptimistic<int>(f1.CreateSignal(1));
                    ref var alias = ref state;
                    alias = f2.CreateOptimistic<int>(f2.CreateSignal(2));
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    // `Changed += handler` executes the custom add accessor immediately, under the same
    // evaluation lock as the computation itself.
    [Fact]
    public async Task SetHiddenInACustomEventAccessor_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static readonly MemoFactory F = new();
                private static readonly Signal<int> V = F.CreateSignal(1);

                private static event Action Changed
                {
                    add { _ = V.Set(2); }
                    remove { }
                }

                public void M()
                {
                    F.CreateMemoizR(async () => { Changed += () => { }; return 0; });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DeconstructionReassignedState_KeepsTheDiagnostic()
    {
        // `(state, _) = ...` rebinds state to another factory's view before the call: the
        // stale initializer must not prove provenance.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        var state = f1.CreateOptimistic<int>(f1.CreateSignal(1));
                        (state, _) = (f2.CreateOptimistic<int>(f2.CreateSignal(2)), 0);
                        await ctx.Apply(state, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task StateReassignedAfterTheCall_KeepsTheSuppression()
    {
        // The reassignment is straight-line AFTER the Apply read: the value already passed
        // came from the f1 initializer, so the provably-disjoint suppression must hold.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        var state = f1.CreateOptimistic<int>(f1.CreateSignal(1));
                        await ctx.Apply(state, x => { _ = other.Set(2); return x; });
                        state = f1.CreateOptimistic<int>(f1.CreateSignal(2));
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ReassignedStateAlias_KeepsTheDiagnostic()
    {
        // The state variable is reassigned from another factory before the call: the stale
        // initializer must not prove provenance, or the suppression would drop a diagnostic
        // the runtime contradicts.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var state = f1.CreateOptimistic<int>(f1.CreateSignal(1));
                    state = f2.CreateOptimistic<int>(f2.CreateSignal(1));
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
    }

    [Fact]
    public async Task AliasedStateArgument_ProvesTheHostFactory()
    {
        // The state reaches Apply through a variable-to-variable alias: provenance must chase
        // initializers until the creation, or disjoint factories would keep a false warning.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var s0 = f1.CreateOptimistic<int>(f1.CreateSignal(1));
                    var state = s0;
                    var other = f2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ProvablyCrossFactorySetInsidePatch_IsNotFlagged()
    {
        // A patch's flow locks the context of the factory that created the OPTIMISTIC STATE
        // (the Apply receiver is the action context, which belongs to no factory), so the
        // cross-factory suppression must resolve the host from the state argument: the Set
        // targets f2's context while the patch evaluates under f1's.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var state = f1.CreateOptimistic<int>(f1.CreateSignal(1));
                    var other = f2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
    [Fact]
    public async Task AliasedReturnedPatch_IsResolved()
    {
        // The factory parks the patch in a local alias before returning it: the return
        // resolves through the alias and the parameter back to the call-site lambda, whose
        // Set runs under the evaluation lock like any inline patch's.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int, int> Make(Func<int, int> p) { var q = p; return q; }
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, Make(x => { _ = v.Set(2); return x; }));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }
    [Fact]
    public async Task AliasedComputedPropertyPatch_SetIsFlagged()
    {
        // The patch is copied out of the computed property before Apply: the alias resolves
        // to the getter's returned lambda, whose Set throws under the evaluation lock.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private readonly MemoFactory f = new();
                private readonly Signal<int> v;

                private Func<int, int> Patch
                {
                    get
                    {
                        return x => { _ = v.Set(2); return x; };
                    }
                }

                public C()
                {
                    v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = Patch;
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInSurvivingRebindPatch_IsFlagged()
    {
        // The declaration initializer is definitely overwritten before Apply: the surviving
        // write's lambda is the stored patch, and its Set throws exactly like an inline one.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = static x => x;
                        patch = x => { _ = v.Set(2); return x; };
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInsideOverwrittenForwardedHandoff_IsNotFlagged()
    {
        // Build's assembled delegate is definitely overwritten before Provide returns: its
        // Set can never be the stored patch, so it must not be flagged.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    void Build(out Func<int, int> d) => d = x => { _ = v.Set(2); return x; };
                    void Provide(out Func<int, int> d)
                    {
                        Build(out d);
                        d = static x => x;
                    }
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch;
                        Provide(out patch);
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
    [Fact]
    public async Task SetInInvokedCapturedDelegate_IsFlagged()
    {
        // The patch synchronously invokes a captured delegate: its body executes under the
        // same evaluation lock, so the Set throws exactly like an inline one.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int, int> d = x => { _ = v.Set(2); return x; };
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => d(x));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInOverwrittenStaleInitializer_IsNotFlagged()
    {
        // The mutating initializer is definitely overwritten before Apply: the overlay can
        // only store the safe overwrite, so the stale Set must not be charged to this call.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = x => { _ = v.Set(2); return x; };
                        patch = static x => x;
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NestedFactoryReturnedPatch_KeepsCallSiteProvenance()
    {
        // Make forwards its signal into Inner, whose returned patch writes it: the nested
        // map proves the disjoint factory's signal safe, while the host factory's own stays
        // flagged.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var mine = f1.CreateSignal(1);
                    var state = f1.CreateOptimistic<int>(mine);
                    Func<int, int> Inner(Signal<int> t) => x => { _ = t.Set(1); return x; };
                    Func<int, int> Make(Signal<int> s) { return Inner(s); }
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, Make(other));
                    });
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, Make(mine));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }
    [Fact]
    public async Task SetInNullConditionalInvokedDelegate_IsFlagged()
    {
        // `d?.Invoke()` still executes the delegate under the evaluation lock when non-null:
        // the conditional-access placeholder resolves to the real delegate expression.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int> d = () => { _ = v.Set(1); return 0; };
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { d?.Invoke(); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SetInFactoryReturnedInvokedDelegate_IsFlagged()
    {
        // `Get()(x)` runs whatever the same-tree factory returned, immediately and under
        // the same lock: the returned lambda's Set throws like an inline one.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int, int> Get() => x => { _ = v.Set(1); return x; };
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => Get()(x));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ComputedStateProperty_KeepsCrossFactoryProof()
    {
        // The Apply state comes from a computed property whose getter always builds on f1:
        // the Set targets a disjoint unkeyed factory's signal, so it locks another context
        // and cannot throw here.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;
            using MemoizR.Reactive;

            public class C
            {
                private static readonly MemoFactory f1 = new MemoFactory();

                private static OptimisticState<int> State => f1.CreateOptimistic<int>(f1.CreateSignal(1));

                public void M()
                {
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(State, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
    [Fact]
    public async Task SetInInvokedComputedPropertyDelegate_IsFlagged()
    {
        // The patch invokes the delegate a computed property hands back: the getter-returned
        // lambda executes under the same evaluation lock, so its Set throws.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private readonly MemoFactory f = new();
                private readonly Signal<int> v;

                private Func<int> Step
                {
                    get
                    {
                        return () => { _ = v.Set(2); return 0; };
                    }
                }

                public C()
                {
                    v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Step());
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ConditionalReturnFactoryArm_IsChased()
    {
        // The factory's return picks an arm at Apply time: the direct safe arm must not
        // silence the factory-call arm, whose returned lambda Sets under the lock.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int, int> Make() => x => { _ = v.Set(1); return x; };
                    Func<int, int> Get(bool flag) { return flag ? Make() : static x => x; }
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, Get(true));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ConditionalPatchFactoryArm_IsChased()
    {
        // The Apply argument itself is conditional: the factory-call arm can be the stored
        // patch, so its returns are chased even though the other arm resolves directly.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int, int> Make() => x => { _ = v.Set(1); return x; };
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, p > 0 ? Make() : static x => x);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ComputedIndexerState_KeepsCrossFactoryProof()
    {
        // The Apply state comes from a computed indexer: the getter's return resolves with
        // the call-site factory bound to the index parameter, proving the Set cross-context.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;
            using MemoizR.Reactive;

            public class C
            {
                private OptimisticState<int> this[MemoFactory f] => f.CreateOptimistic<int>(f.CreateSignal(1));

                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(this[f1], x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ComputedSignalTargetProperty_KeepsCrossFactoryProof()
    {
        // The Set target is a computed signal property whose getter always builds on the
        // disjoint unkeyed factory: the Set locks that other context and cannot throw here.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private static readonly MemoFactory f2 = new MemoFactory();

                private static Signal<int> Other => f2.CreateSignal(1);

                public void M()
                {
                    var f1 = new MemoFactory();
                    var state = f1.CreateOptimistic<int>(f1.CreateSignal(1));
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { _ = Other.Set(2); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SetInDeconstructionOverwrittenInitializer_IsNotFlagged()
    {
        // The deconstruction definitely overwrites the mutating initializer before Apply:
        // only the paired safe value can be stored, so the stale Set is not charged.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        Func<int, int> patch = x => { _ = v.Set(2); return x; };
                        (patch, _) = ((Func<int, int>)(static x => x), 0);
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
    [Fact]
    public async Task ConditionalComputedStateGetter_KeepsCrossFactoryProof()
    {
        // Every arm of the computed state getter builds on f1: the host context is provable
        // even though the getter returns a conditional, so the disjoint Set cannot throw.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;
            using MemoizR.Reactive;

            public class C
            {
                private static readonly MemoFactory f1 = new MemoFactory();

                private bool flag;

                private OptimisticState<int> State => flag
                    ? f1.CreateOptimistic<int>(f1.CreateSignal(1))
                    : f1.CreateOptimistic<int>(f1.CreateSignal(2));

                public void M()
                {
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(State, x => { _ = other.Set(2); return x; });
                    });
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InvokedDelegateSignalArgument_KeepsCallSiteProvenance()
    {
        // The invoked delegate's parameter binds to the call's argument: the disjoint
        // factory's signal is proven cross-context, while the host's own stays flagged.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var other = f2.CreateSignal(1);
                    var mine = f1.CreateSignal(1);
                    var state = f1.CreateOptimistic<int>(mine);
                    Action<Signal<int>> step = s => { _ = s.Set(2); };
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { step(other); return x; });
                    });
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { step(mine); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InvokedFactoryInitializedDelegate_SetIsFlagged()
    {
        // The invoked local was initialized from a same-tree factory: the returned lambda
        // executes under the evaluation lock, so its Set throws.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int, int> Make() => x => { _ = v.Set(2); return x; };
                    Func<int, int> d = Make();
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => d(x));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }
    [Fact]
    public async Task ConditionalInvokedCalleeFactoryArm_IsChased()
    {
        // One callee arm resolves directly; the other is a factory call whose returned
        // lambda Sets. The resolvable arm must not account for its sibling.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int> Make() => () => { _ = v.Set(2); return 0; };
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + (p > 0 ? static () => 0 : Make())());
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task NamedInvokeArguments_BindByParameter()
    {
        // The invoke names its arguments out of declaration order: `mine` must bind to the
        // host factory's signal, which is what makes this Set throw.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public delegate void Step(Signal<int> mine, Signal<int> other);

            public class C
            {
                public void M()
                {
                    var f1 = new MemoFactory();
                    var f2 = new MemoFactory();
                    var remote = f2.CreateSignal(1);
                    var local = f1.CreateSignal(1);
                    var state = f1.CreateOptimistic<int>(local);
                    Step step = (mine, other) => { _ = mine.Set(2); };
                    f1.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => { step(other: remote, mine: local); return x; });
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }
    [Fact]
    public async Task ParameterAssignedPatch_SetIsFlagged()
    {
        // The patch lives in a PARAMETER slot assigned before Apply: the assignment
        // dominates the read, so the factory-returned body is what replays.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                private readonly MemoFactory f = new();

                public void M(Func<int, int> patch)
                {
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    Func<int, int> Make(Signal<int> s) => x => { _ = s.Set(2); return x; };
                    patch = Make(v);
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, patch);
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ConditionallyReboundInvokedParameter_KeepsTheCallerDelegate()
    {
        // The helper rebinds its delegate parameter only on one path: the caller's delegate
        // still runs on the other, so its Set is a candidate.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    var v = f.CreateSignal(1);
                    var state = f.CreateOptimistic<int>(v);
                    static int Run(Func<int> g, bool replace)
                    {
                        if (replace)
                        {
                            g = static () => 0;
                        }

                        return g();
                    }
                    f.CreateAction<int>(async (p, ctx) =>
                    {
                        await ctx.Apply(state, x => x + Run(() => { _ = v.Set(2); return 0; }, false));
                    });
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR003", diagnostic.Id);
        Assert.Contains("Signal<int>.Set", diagnostic.GetMessage());
    }
}
