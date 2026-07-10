using Microsoft.CodeAnalysis;

namespace MemoizR.Analyzers.Tests;

// Contracts of MZR005: after a local/parameter is wrapped in Sending<T> (constructor or
// Sending.Transfer), later uses of that variable in the same method are flagged in source
// order, stopping at a reassignment. Best-effort by design -- the runtime single-consumption
// check in Sending<T>.Receive is the receiver-side backstop.
public class UseAfterTransferAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => AnalyzerTestHarness.AnalyzeAsync(source, new UseAfterTransferAnalyzer());

    [Fact]
    public async Task UseAfterTransfer_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var f = new MemoFactory(options: MemoFactoryOptions.StrictSendableChecks);
                    var list = new List<int> { 1 };
                    var signal = f.CreateSignal(Sending.Transfer(list));
                    list.Add(2); // the receiver may already own it on another flow
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ReassignmentWhoseRhsReadsTheTransferredValue_IsFlagged()
    {
        // `list = Clone(list)` LOOKS like a fresh value, but the RHS reads the transferred one
        // to build the replacement -- exactly a use after transfer.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    list = new List<int>(list); // reads the transferred list
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ConditionalReassignment_DoesNotSilenceTheLaterUse()
    {
        // On the `reset == false` path the later Add still touches the transferred list: only a
        // reassignment that definitely executes may end the tracking.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M(bool reset)
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    if (reset)
                    {
                        list = new List<int>();
                    }

                    list.Add(2);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task NullForgivingTransfer_IsStillATransfer()
    {
        // `Sending.Transfer(list!)` hands off the same variable; the null-forgiving operator
        // must not hide the transfer from the rule.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    List<int>? list = new List<int> { 1 };
                    var sending = Sending.Transfer(list!);
                    list.Add(2);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ReassignmentInACatchHandler_DoesNotSilenceTheLaterUse()
    {
        // The try spans the transfer, but the catch is a SIBLING ARM of it: on the
        // no-exception path the reassignment never ran and the later Add still touches the
        // transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>>? M()
                {
                    var list = new List<int> { 1 };
                    Sending<List<int>>? sending = null;
                    try
                    {
                        sending = Sending.Transfer(list);
                    }
                    catch
                    {
                        list = new List<int>();
                    }

                    list.Add(1);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ReassignmentInAFinally_IsDefinite_AndEndsTracking()
    {
        // A finally arm ALWAYS executes: unlike a catch, its reassignment is as definite as
        // straight-line code, so the later use touches the fresh value.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>>? M()
                {
                    var list = new List<int> { 1 };
                    Sending<List<int>>? sending = null;
                    try
                    {
                        sending = Sending.Transfer(list);
                    }
                    finally
                    {
                        list = new List<int>();
                    }

                    list.Add(1);
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task OutArgument_Reinitializes_AndEndsTracking()
    {
        // An out argument is definite assignment the callee cannot read: like `list = ...`, it
        // gives the variable a fresh value, so the later use is not a use-after-transfer.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    Reset(out list);
                    list.Add(1);
                    return sending;
                }

                private static void Reset(out List<int> value)
                {
                    value = new List<int>();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ConditionalOutArgument_DoesNotSilenceTheLaterUse()
    {
        // Same sibling-arm rule as a conditional `list = ...`: on the false path the out-call
        // never ran and the later use still touches the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M(bool reset)
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    if (reset)
                    {
                        Reset(out list);
                    }

                    list.Add(1);
                    return sending;
                }

                private static void Reset(out List<int> value)
                {
                    value = new List<int>();
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task TransferInsideADeferredCallback_DoesNotPoisonTheOuterFlow()
    {
        // The callback may run later or never: outer statements are not sequenced after its
        // transfer, so the outer Add is not a use-after-transfer.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Action M()
                {
                    var list = new List<int> { 1 };
                    Action later = () => { _ = Sending.Transfer(list); };
                    list.Add(1);
                    return later;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UseAfterTransfer_InsideTheSameCallback_IsStillFlagged()
    {
        // Within one callback body source order does imply execution order: the callback's own
        // later use of its own transfer is the ordinary MZR005 case.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Action M()
                {
                    var list = new List<int> { 1 };
                    return () =>
                    {
                        var sending = Sending.Transfer(list);
                        list.Add(1);
                        _ = sending;
                    };
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task UsesBeforeTransfer_AndReassignedVariables_AreNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    list.Add(2); // before the transfer: fine
                    var sending = new Sending<List<int>>(list);
                    list = new List<int>(); // fresh value: the transferred one is gone
                    list.Add(3);
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
}
