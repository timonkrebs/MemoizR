using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoizR.Analyzers.Tests;

// Compiles a snippet in-memory against the runtime's assemblies (which include the real MemoizR
// assemblies, project-referenced by this test project) and runs one analyzer over it. Asserting
// zero compile errors first keeps the analyzer assertions honest: a diagnostic count on code that
// does not compile proves nothing.
internal static class AnalyzerTestHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(BuildReferences);

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // TRUSTED_PLATFORM_ASSEMBLIES lists every assembly resolvable by this test process: the
        // framework plus all project/package dependencies, MemoizR included. Using it wholesale
        // avoids hand-maintaining the closure (Nito, etc.).
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return [.. paths.Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    }

    public static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, DiagnosticAnalyzer analyzer)
    {
        return AnalyzeAsync([source], analyzer);
    }

    // Most contracts are one class: the usual usings plus `public class C` around the members.
    public static string InClassC(string members, string usings)
    {
        return $"{usings}\n\npublic class C\n{{\n{members}\n}}";
    }

    public static Diagnostic AssertSingle(ImmutableArray<Diagnostic> diagnostics, string id)
    {
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(id, diagnostic.Id);
        return diagnostic;
    }

    // Multi-file overload: some rules are scoped per FILE (MZR004's using-directive mandate),
    // so proving cross-file behavior (a centralized GlobalUsings.cs) needs separate trees.
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string[] sources, DiagnosticAnalyzer analyzer)
    {
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))).ToArray();
        var compilation = CSharpCompilation.Create(
            "AnalyzerTestSnippet",
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(compileErrors.Count == 0, $"snippet does not compile: {string.Join("; ", compileErrors)}");

        return await compilation
            .WithAnalyzers([analyzer])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);
    }
}
