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
}
