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
}
