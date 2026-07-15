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
        // The value's own runtime type IS what ValidateWrittenValues checks, so here (and only
        // here) the hint may suggest it.
        Assert.Contains("enable MemoFactoryOptions.ValidateWrittenValues", diagnostic.GetMessage());
    }

    [Fact]
    public async Task NonSealedElementInsideASendableContainer_IsHinted()
    {
        // The smuggle surface hides inside the green-listed container: ImmutableArray<OpenBase>
        // passes MZR001, but each element can still be a mutable subclass behind the upcast.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public record OpenPerson(string Name);

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(ImmutableArray.Create(new OpenPerson("a")));
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR006", diagnostic.Id);
        Assert.Contains("OpenPerson", diagnostic.GetMessage());
        // The signal-write guard checks the written ARRAY's runtime type only; suggesting it
        // for the nested element would promise a check that can never see it.
        Assert.DoesNotContain("enable MemoFactoryOptions.ValidateWrittenValues", diagnostic.GetMessage());
        Assert.Contains("not contents nested inside it", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SendingTransfers_AreNotSmugglingSurfaces()
    {
        // Sending<T> DELIBERATELY wraps a non-Sendable payload for transfer (the SE-0430
        // analog); hinting about the payload's sealedness would misread the escape hatch as
        // shared Sendable state.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(Sending.Transfer(new List<int>()));
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SmuggleSurfaces_BuriedArbitrarilyDeep_AreStillHinted()
    {
        // No depth cliff: the declared type tree is finite, so the walk terminates on its own
        // -- a cap would silently drop the hint exactly for the compositions MZR001 accepts.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Immutable;
            using MemoizR;

            public record OpenPerson(string Name);

            public class C
            {
                public void M(ImmutableArray<ImmutableArray<ImmutableArray<ImmutableArray<ImmutableArray<OpenPerson>>>>> value)
                {
                    var f = new MemoFactory();
                    f.CreateSignal(value);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR006", diagnostic.Id);
        Assert.Contains("OpenPerson", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DisabledChecksFactory_GetsNoSmugglingHint()
    {
        // Smuggling is a hole in the Sendable checks; a factory that visibly opted out of them
        // has nothing to smuggle past, so the hint would be pure noise.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public record OpenPerson(string Name);

            public class C
            {
                public void M()
                {
                    var lax = new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks);
                    lax.CreateSignal(new OpenPerson("a"));
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task MemoCreations_DoNotPromiseTheSignalOnlyRuntimeGuard()
    {
        // ValidateWrittenValues checks SIGNAL writes only; the hint on memo creations must not
        // suggest a guard that does not apply there.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public record OpenPerson(string Name);

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateMemoizR(async () => new OpenPerson("a"));
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("signal writes only", diagnostic.GetMessage());
        Assert.DoesNotContain("enable MemoFactoryOptions.ValidateWrittenValues", diagnostic.GetMessage());
    }

    [Fact]
    public async Task MemberTypes_OfSealedSendableDtos_AreSmuggleSurfaces()
    {
        // Box is sealed and structurally Sendable, but its member type carries the hole:
        // new Box(new MutableChild()) passes MZR001, and ValidateWrittenValues only sees the
        // runtime type Box -- the member walk must surface OpenPerson with the nested wording.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public record OpenPerson(string Name);

            public sealed record Box(OpenPerson Value);

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new Box(new OpenPerson("a")));
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR006", diagnostic.Id);
        Assert.Contains("OpenPerson", diagnostic.GetMessage());
        Assert.DoesNotContain("enable MemoFactoryOptions.ValidateWrittenValues", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InheritedMemberTypes_AreSmuggleSurfacesToo()
    {
        // The member walk follows the base chain like the classifier: the hole hides on the
        // inherited member exactly like on a declared one.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public record OpenPerson(string Name);

            public class BoxBase
            {
                public OpenPerson? Value { get; init; }
            }

            public sealed class Box : BoxBase
            {
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal(new Box());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR006", diagnostic.Id);
        Assert.Contains("OpenPerson", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SendableAbstractBase_IsStillASmuggleSurface()
    {
        // [Sendable] lets an abstract base PASS MZR001 (Error), but the attribute is
        // deliberately not inherited -- the assertion binds the base author, not every
        // subclass -- so the smuggle hole reopens exactly where the Error rule went quiet.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            [Sendable]
            public abstract class Base
            {
            }

            public sealed class Child : Base
            {
            }

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory();
                    f.CreateSignal<Base>(new Child());
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR006", diagnostic.Id);
        Assert.Contains("Base", diagnostic.GetMessage());
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
