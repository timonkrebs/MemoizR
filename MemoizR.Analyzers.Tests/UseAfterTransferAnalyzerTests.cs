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
