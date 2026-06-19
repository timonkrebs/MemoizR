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

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, DiagnosticAnalyzer analyzer)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "AnalyzerTestSnippet",
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(compileErrors.Count == 0, $"snippet does not compile: {string.Join("; ", compileErrors)}");

        return await compilation
            .WithAnalyzers([analyzer])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);
    }
}
