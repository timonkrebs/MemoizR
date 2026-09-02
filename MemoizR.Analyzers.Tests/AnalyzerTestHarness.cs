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

    // ConsoleApplication is for top-level-statement snippets, which cannot compile in a library.
    public static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, DiagnosticAnalyzer analyzer, OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
        => AnalyzeAsync([source], analyzer, outputKind);

    // Multiple sources compile as separate syntax trees: for pinning the same-tree-only
    // resolution boundaries (a method group whose body lives in another file).
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string[] sources, DiagnosticAnalyzer analyzer, OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))).ToArray();
        var compilation = CSharpCompilation.Create(
            "AnalyzerTestSnippet",
            trees,
            References.Value,
            new CSharpCompilationOptions(outputKind, nullableContextOptions: NullableContextOptions.Enable));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(compileErrors.Count == 0, $"snippet does not compile: {string.Join("; ", compileErrors)}");

        return await compilation
            .WithAnalyzers([analyzer])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);
    }
}
