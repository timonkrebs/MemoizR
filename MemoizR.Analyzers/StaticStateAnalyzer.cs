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
        ImmutableArray.Create(DiagnosticDescriptors.MutableStaticState, DiagnosticDescriptors.NonSealedValueType);

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
            ReportSmuggleSurfacesIn(context, symbol);
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.MutableStaticState,
            symbol.Locations.FirstOrDefault() ?? Location.None,
            kind,
            symbol.Name,
            finding));
    }

    // A static that PASSED the slot rule can still smuggle: a readonly static of a non-sealed
    // (or [Sendable]-asserted abstract) Sendable type stores whatever subclass got upcast in,
    // with no creation site where MZR006 would hint and no runtime write validation ever
    // seeing the slot. Same Info-severity calculus as the creation-site hint.
    private static void ReportSmuggleSurfacesIn(SymbolAnalysisContext context, ISymbol symbol)
    {
        var slotType = symbol switch
        {
            IFieldSymbol { IsConst: false } field => field.Type,
            IPropertySymbol property when SendableSymbolClassifier.HasBackingSlot(property) => property.Type,
            _ => null,
        };

        if (slotType is null)
        {
            return;
        }

        foreach (var (named, _) in SubclassSmugglingAnalyzer.NamedTypesIn(slotType, depth: 0))
        {
            if (SubclassSmugglingAnalyzer.IsSmuggleSurface(named))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.NonSealedValueType,
                    symbol.Locations.FirstOrDefault() ?? Location.None,
                    SendableSymbolClassifier.Display(named),
                    " (a static slot publishes unchecked: ValidateWrittenValues covers signal writes only)"));
            }
        }
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

        if (!SendableSymbolClassifier.HasBackingSlot(property))
        {
            // A computed getter owns no static slot: a fresh value per call shares nothing,
            // and a getter handing out OTHER static state is flagged at that state's own
            // declaration.
            return null;
        }

        var reason = NotSendableReason(property.Type, classifier);
        return reason is null ? null : $"get-only, but of non-Sendable type {SendableSymbolClassifier.Display(property.Type)} ({reason})";
    }

    // MZR001 gives unbound type parameters the benefit of the doubt because the closed
    // instantiation is checked at its own creation site. A static has NO later site: every
    // closed C<T> mints a fresh process-wide slot no rule ever sees again, so a type parameter
    // ANYWHERE in the slot's type (T itself, ImmutableArray<T>, Holder<T>) is unverifiable
    // rather than trusted. [Sendable]-attributed types are the one shield: their thread-safety
    // assertion does not rest on the type arguments -- MemoizR's own nodes are internally
    // synchronized for any T, and a Signal<T> static's closed T IS checked later, at the
    // CreateSignal call that built the instance.
    private static string? NotSendableReason(ITypeSymbol type, SendableSymbolClassifier classifier)
    {
        if (HasUnshieldedTypeParameter(type))
        {
            return "a type parameter is unverifiable in a static: unlike a creation site, no closed instantiation is ever checked";
        }

        return classifier.GetNotSendableReason(type);
    }

    // No depth cap: the type graph is finite and the visited set breaks cycles, so the
    // recursion terminates on its own -- and a cap would have to choose between failing open
    // (a parameter buried one level past it silently trusted) and misreporting deep concrete
    // types as parameters. Besides type arguments, MEMBER types of source-declared types are
    // walked: a nested `sealed class Holder { public T Value ... }` carries the OUTER type
    // parameter on its members, not in its own argument list, and the shared classifier's
    // member walk would accept that T through the exemption this rule exists to remove.
    private static bool HasUnshieldedTypeParameter(ITypeSymbol type)
    {
        return HasUnshieldedTypeParameter(
            type,
            new System.Collections.Generic.HashSet<ITypeSymbol>(SymbolEqualityComparer.Default),
            new System.Collections.Generic.List<INamedTypeSymbol>());
    }

    private static bool HasUnshieldedTypeParameter(
        ITypeSymbol type,
        System.Collections.Generic.HashSet<ITypeSymbol> visited,
        System.Collections.Generic.List<INamedTypeSymbol> path)
    {
        switch (type)
        {
            case ITypeParameterSymbol parameter:
                // `where T : unmanaged` guarantees a no-reference value type for EVERY closed
                // instantiation (reads hand out copies that can alias nothing), and
                // `where T : Enum` guarantees immutable values or immutable boxes -- both are
                // safe without any creation-site check. (A plain `struct` constraint is NOT
                // enough: a struct can carry references to mutable objects.)
                return !parameter.HasUnmanagedTypeConstraint
                    && !parameter.ConstraintTypes.Any(constraint => constraint.SpecialType == SpecialType.System_Enum);
            case IArrayTypeSymbol array:
                return HasUnshieldedTypeParameter(array.ElementType, visited, path);
            case INamedTypeSymbol named
                when visited.Add(named) && !SendableSymbolClassifier.HasSendableAttribute(named):
                if (named.TypeArguments.Any(argument => HasUnshieldedTypeParameter(argument, visited, path)))
                {
                    return true;
                }

                // Member re-instantiation can construct fresh symbols forever (Box<T> exposing
                // Box<List<T>>), so the member walk needs a divergence bound -- counted on the
                // recursion PATH, not globally: sibling instantiations (Outer<int>.Holder next
                // to Outer<string>.Holder next to Outer<T>.Holder) are not recursion and must
                // all be walked, while a divergent chain stacks same-definition ancestors.
                var definition = (INamedTypeSymbol)named.OriginalDefinition;
                var priorNonShrinking = path.Count(other =>
                    SymbolEqualityComparer.Default.Equals(other.OriginalDefinition, definition)
                    && SendableSymbolClassifier.TypeSize(other) <= SendableSymbolClassifier.TypeSize(named));
                if (priorNonShrinking >= 2)
                {
                    return false;
                }

                path.Add(named);
                try
                {
                    return MemberTypesOf(named).Any(memberType => HasUnshieldedTypeParameter(memberType, visited, path));
                }
                finally
                {
                    path.RemoveAt(path.Count - 1);
                }

            default:
                return false;
        }
    }

    private static System.Collections.Generic.IEnumerable<ITypeSymbol> MemberTypesOf(INamedTypeSymbol named)
    {
        if (!named.Locations.Any(location => location.IsInSource))
        {
            yield break; // metadata members are import-limited; framework internals carry no user T
        }

        // The base chain is walked like the classifier walks it: an INHERITED member stores
        // state (and possibly an outer type parameter) exactly like a declared one. Only
        // STORED members contribute -- explicit fields and auto-properties; a computed member
        // (`public T New => default!`) holds no slot, the member-level analog of the
        // top-level computed-getter exemption.
        for (var current = named; current is not null && !IsRootType(current); current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } field:
                        yield return field.Type;
                        break;
                    case IPropertySymbol { IsStatic: false } property when SendableSymbolClassifier.HasBackingSlot(property):
                        yield return property.Type;
                        break;
                }
            }
        }
    }

    private static bool IsRootType(INamedTypeSymbol type)
    {
        return type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;
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
