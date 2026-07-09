using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR006 (Info): a non-sealed class type at a creation site can smuggle a mutable
// subclass past the declared-type Sendable checks; sealed types, value types, and green-listed
// framework types (Uri is not sealed) stay quiet.
public class SubclassSmugglingAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new SubclassSmugglingAnalyzer());

    [Fact]
    public async Task NonSealedClassAtCreationSite_GetsTheInfoHint()
    {
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public record OpenPerson(string Name); // records default to non-sealed

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new OpenPerson("a"));
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR006", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("OpenPerson", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SealedValueAndGreenListedTypes_AreQuiet()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using MemoizR;

            public sealed record SealedPerson(string Name);

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new SealedPerson("a"));
                    f.CreateSignal(1); // value type
                    f.CreateSignal(new Uri("https://x")); // green-listed framework type, though not sealed
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
}
