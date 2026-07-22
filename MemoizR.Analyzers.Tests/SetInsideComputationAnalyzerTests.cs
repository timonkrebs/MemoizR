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
}
