using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoizR.Analyzers;

// MZR004, the SE-0412 analog proper: Swift 6 rejects every non-isolated mutable global, because
// a global is reachable from every isolation domain. Here the domains are concurrently running
// flows, and a static next to a reactive graph is reachable from all of them: a MUTABLE SLOT
// (non-readonly field, settable property, event) races on the slot itself; a readonly slot of a
// NON-SENDABLE TYPE shares one mutable object graph. Both are exactly the states the library's
// own model wants lifted into a Signal/EagerRelativeSignal -- which, like all MemoizR nodes, is
// [Sendable] and therefore fine to hold in a static.
//
// Scoping keeps the rule inside MemoizR's mandate: it fires only when the compilation references
// the real MemoizR assembly AND the static's file uses MemoizR (a using directive for the
// MemoizR namespaces). A project's unrelated corners stay unflagged.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticStateAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.MutableStaticState);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationStart =>
        {
            // Only meaningful in compilations that actually use the library (the analyzer ships
            // in the nupkg, but transitive references can drag it further than intended).
            if (compilationStart.Compilation.GetTypeByMetadataName("MemoizR.MemoFactory") is not INamedTypeSymbol factory
                || !FactoryMethods.IsLibraryType(factory))
            {
                return;
            }

            var classifier = new SendableSymbolClassifier();
            var treeUsesMemoizR = new ConcurrentDictionary<SyntaxTree, bool>();

            // A `global using MemoizR;` anywhere puts MemoizR in scope for EVERY file, so the
            // per-tree check must not exempt projects that centralize their usings. One shallow
            // scan per compilation, computed lazily on the first static symbol seen.
            var compilation = compilationStart.Compilation;
            var hasGlobalMemoizRUsing = new System.Lazy<bool>(() =>
                compilation.SyntaxTrees.Any(tree => MemoizRUsingsIn(tree).Any(directive => directive.GlobalKeyword != default)));

            compilationStart.RegisterSymbolAction(
                symbolContext => Analyze(symbolContext, classifier, treeUsesMemoizR, hasGlobalMemoizRUsing),
                SymbolKind.Field, SymbolKind.Property, SymbolKind.Event);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, SendableSymbolClassifier classifier, ConcurrentDictionary<SyntaxTree, bool> treeUsesMemoizR, System.Lazy<bool> hasGlobalMemoizRUsing)
    {
        var symbol = context.Symbol;
        if (!symbol.IsStatic || symbol.IsImplicitlyDeclared)
        {
            return;
        }

        if (!hasGlobalMemoizRUsing.Value && !IsInMemoizRUsingFile(symbol, treeUsesMemoizR))
        {
            return;
        }

        var (kind, finding) = symbol switch
        {
            IFieldSymbol field => ("field", ClassifyField(field, classifier)),
            IPropertySymbol property => ("property", ClassifyProperty(property, classifier)),
            IEventSymbol => ("event", "a mutable subscription surface (its backing delegate changes on every add/remove)"),
            _ => ("", null),
        };

        if (finding is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.MutableStaticState,
            symbol.Locations.FirstOrDefault() ?? Location.None,
            kind,
            symbol.Name,
            finding));
    }

    private static string? ClassifyField(IFieldSymbol field, SendableSymbolClassifier classifier)
    {
        if (field.IsConst)
        {
            return null; // compile-time constant: immutable by construction
        }

        if (!field.IsReadOnly)
        {
            return "a mutable slot";
        }

        var reason = NotSendableReason(field.Type, classifier);
        return reason is null ? null : $"readonly, but of non-Sendable type {SendableSymbolClassifier.Display(field.Type)} ({reason})";
    }

    private static string? ClassifyProperty(IPropertySymbol property, SendableSymbolClassifier classifier)
    {
        if (property.GetMethod is null)
        {
            return null; // set-only oddity: nothing shared can be read back
        }

        if (property.SetMethod is { IsInitOnly: false })
        {
            return "a settable slot";
        }

        var reason = NotSendableReason(property.Type, classifier);
        return reason is null ? null : $"get-only, but of non-Sendable type {SendableSymbolClassifier.Display(property.Type)} ({reason})";
    }

    // MZR001 gives unbound type parameters the benefit of the doubt because the closed
    // instantiation is checked at its own creation site. A static has NO later site: every
    // closed C<T> mints a fresh process-wide slot no rule ever sees again, so here a type
    // parameter is unverifiable rather than trusted.
    private static string? NotSendableReason(ITypeSymbol type, SendableSymbolClassifier classifier)
    {
        if (type is ITypeParameterSymbol)
        {
            return "a type parameter is unverifiable in a static: unlike a creation site, no closed instantiation is ever checked";
        }

        return classifier.GetNotSendableReason(type);
    }

    // The mandate boundary: the static's FILE must use MemoizR. Using directives sit at the
    // compilation-unit or namespace level, so the scan is shallow; the verdict is cached per
    // tree (symbol actions fire per static, and many statics share a file).
    private static bool IsInMemoizRUsingFile(ISymbol symbol, ConcurrentDictionary<SyntaxTree, bool> treeUsesMemoizR)
    {
        var tree = symbol.Locations.FirstOrDefault()?.SourceTree;
        if (tree is null)
        {
            return false;
        }

        return treeUsesMemoizR.GetOrAdd(tree, static t => MemoizRUsingsIn(t).Any());
    }

    private static System.Collections.Generic.IEnumerable<UsingDirectiveSyntax> MemoizRUsingsIn(SyntaxTree tree)
    {
        return tree.GetRoot()
            .DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Where(directive => directive.Name?.ToString() is { } name
                && (name == "MemoizR" || name.StartsWith("MemoizR.", System.StringComparison.Ordinal)));
    }
}
