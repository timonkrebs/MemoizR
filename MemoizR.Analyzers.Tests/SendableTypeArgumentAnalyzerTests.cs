using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR001: every value-bearing factory creation is checked, the verdicts mirror the
// runtime SendableChecker (green-lists, [Sendable] trust, structural walk), type parameters get
// the benefit of the doubt, and the message names the offending member.
public class SendableTypeArgumentAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new SendableTypeArgumentAnalyzer());

    [Fact]
    public async Task MutableSignalType_IsFlagged_WithTheStructuralReason()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
        Assert.Contains("List<int>", diagnostic.GetMessage());
        Assert.Contains("not Sendable", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ImmutableTypes_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public sealed record Person(string Name, int Age);

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(1);
                    f.CreateSignal("s");
                    f.CreateSignal(new Person("a", 1));
                    f.CreateSignal(ImmutableArray.Create(1, 2));
                    f.CreateMemoizR(async () => 1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task RecordWithSettableProperty_IsFlagged_NamingTheProperty()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public sealed record Mutable
            {
                public string Name { get; set; } = "";
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => new Mutable());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("'Name'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ImmutableCollectionBuilder_IsFlagged_NotBlessedByNamespace()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(ImmutableList.CreateBuilder<int>()); // Builder is mutable
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task TypeWithVisibleEvent_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public sealed class HasEvent { public event Action? Changed; }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new HasEvent()); // subscribing mutates the shared instance
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }

    [Fact]
    public async Task SendableAttribute_IsTrusted()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            [Sendable]
            public sealed class TrustedMutable
            {
                public int Count { get; set; }
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new TrustedMutable());
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UnboundTypeParameter_GetsTheBenefitOfTheDoubt()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public Signal<T> Make<T>(MemoFactory f, T value) => f.CreateSignal(value);
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ConcurrentRace_ResolverResult_IsChecked()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateConcurrentRace<int, List<int>>(
                        async () => new List<int>(),
                        async (_, _) => 1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("List<int>", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ActorEngineCreations_AreChecked()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateActorSignal(new List<int>());
                    f.CreateActorMemoizR(async () => new Dictionary<string, int>());
                    f.CreateActorSignal(1); // fine
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MZR001", d.Id));
    }

    [Fact]
    public async Task ConcurrentMap_ElementType_IsChecked()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateConcurrentMap<List<int>>(async _ => new List<int>());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR001", diagnostic.Id);
    }
}
