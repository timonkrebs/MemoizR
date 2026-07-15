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
    public async Task TransferInOneArm_SiblingArmUses_AreUnreachable()
    {
        // The arms are mutually exclusive: no execution path runs the else's Add after the
        // then's transfer, so there is nothing to flag.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>>? M(bool move)
                {
                    var list = new List<int> { 1 };
                    Sending<List<int>>? sending = null;
                    if (move)
                    {
                        sending = Sending.Transfer(list);
                    }
                    else
                    {
                        list.Add(1);
                    }

                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ConditionalUse_OnAReachablePath_IsStillFlagged()
    {
        // Unlike a sibling arm, a branch the transfer precedes entirely IS reachable after it:
        // the log==true path runs the Add on the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M(bool log)
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    if (log)
                    {
                        list.Add(1);
                    }

                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task UsesDominatedByAConditionalReinitialization_AreClean()
    {
        // Inside the arm, after the reassignment, every path that reaches the Add sees the
        // fresh list (reset == false never enters the arm): nothing touches the transferred
        // value.
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
                        list.Add(1);
                    }

                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UsesAfterTheDominatingArm_AreStillFlagged()
    {
        // The domination ends with the arm: past the if, the reset == false path still hands
        // the transferred list to the Add.
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
                        list.Add(1);
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
    public async Task OutArgument_WithASiblingArgumentReadingTheVariable_IsFlagged()
    {
        // The out-assignment happens only when the callee runs, AFTER every argument was
        // evaluated: the second argument still reads the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    Reset(out list, list);
                    return sending;
                }

                private static void Reset(out List<int> value, List<int> seed)
                {
                    value = new List<int>(seed);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CallbackReassignment_IsDeferred_TheOuterUseIsStillFlagged()
    {
        // The callback may never run before the outer Add, so its reassignment cannot end the
        // outer tracking -- while within the callback's own body it dominates as usual.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    Action later = () =>
                    {
                        list = new List<int>();
                        list.Add(1);
                    };

                    list.Add(2);
                    _ = later;
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task TransferInTheCondition_DominatesBothArms()
    {
        // The condition runs BEFORE either arm: the then-arm's Add executes after the handoff
        // on every path that reaches it, so it is not a sibling-arm exclusion.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    if (Sending.Transfer(list) != null)
                    {
                        list.Add(1);
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task TransferThatExitsTheFlow_HasNoSenderSideContinuation()
    {
        // `return Sending.Transfer(list);` never falls through: the fallthrough path never
        // transferred, so the later Add is unreachable on the path that did.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>>? M(bool move)
                {
                    var list = new List<int> { 1 };
                    if (move)
                    {
                        return Sending.Transfer(list);
                    }

                    list.Add(1);
                    return null;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SwitchGuardTransfers_AreVisibleToLaterArms()
    {
        // A failing `when` guard falls through to later cases with the transfer already done:
        // the default arm's Add is a real use after transfer, not a sibling-arm exclusion.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(int n)
                {
                    var list = new List<int> { 1 };
                    switch (n)
                    {
                        case 0 when Sending.Transfer(list) is null:
                            break;
                        default:
                            list.Add(1);
                            break;
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task SwitchValueTransfers_DominateTheArms()
    {
        // The switch VALUE evaluates before the selected case -- and here the value IS the
        // transfer, whose exclusive span end must still count as covering the position.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    switch (Sending.Transfer(list))
                    {
                        case var _:
                            list.Add(1);
                            break;
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CoalesceAssignmentTransfer_IsTracked()
    {
        // The lazy-init handoff aliases the variable to the transferred value on BOTH paths
        // (already non-null, or just assigned): the later Add is a use-after-transfer.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    List<int>? list = null;
                    var sending = Sending.Transfer(list ??= new List<int> { 1 });
                    list.Add(1);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task EscapingExpressions_StillReadTheirLaterArguments()
    {
        // The return expression evaluates its remaining arguments AFTER the transfer, before
        // the method exits: the second argument is a use on the transfer path.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public (Sending<List<int>>, List<int>) M()
                {
                    var list = new List<int> { 1 };
                    return (Sending.Transfer(list), list);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task BreaksBetweenTransferAndReassignment_KeepTracking()
    {
        // The break exits the loop past the reassignment: on the skip path the code after the
        // loop still holds the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool cond, bool skip)
                {
                    var list = new List<int> { 1 };
                    while (cond)
                    {
                        _ = Sending.Transfer(list);
                        if (skip)
                        {
                            break;
                        }

                        list = new List<int>();
                    }

                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task BreaksThatExitBeforeTheReassignment_SkipNothing()
    {
        // The switch closes before the reassignment: its break cannot jump past it, so the
        // reassignment stays definite and the later use is clean.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(int n)
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    switch (n)
                    {
                        case 1:
                            n++;
                            break;
                    }

                    list = new List<int>();
                    list.Add(1);
                    _ = sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SiblingArmBranches_CannotSkipOnTheTransferPath()
    {
        // The else-break never runs on the path that transferred: the reassignment at the end
        // of the loop body is definite there, so the code after the loop sees a fresh list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool c, bool move)
                {
                    var list = new List<int> { 1 };
                    while (c)
                    {
                        if (move)
                        {
                            _ = Sending.Transfer(list);
                        }
                        else
                        {
                            break;
                        }

                        list = new List<int>();
                    }

                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CoalescedTransferOperands_AreTracked()
    {
        // On the normal path the coalesce hands off `list` itself: the later Add is a
        // use-after-transfer even though the argument is not a bare variable reference.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M(List<int>? list)
                {
                    var sending = Sending.Transfer(list ?? throw new InvalidOperationException());
                    list.Add(1);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CatchHandlers_AfterACompletedTransfer_AreUnreachable()
    {
        // Nothing follows the transfer inside the try: a completed handoff skips the handlers,
        // and a throw from the transfer expression itself means no wrapper escaped.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        _ = Sending.Transfer(list);
                    }
                    catch
                    {
                        list.Add(1);
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CatchHandlers_WithThrowingCodeAfterTheTransfer_AreStillScanned()
    {
        // The call after the transfer can throw INTO the handler with the wrapper already
        // escaped: the handler's use stays reportable.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        _ = Sending.Transfer(list);
                        MayThrow();
                    }
                    catch
                    {
                        list.Add(1);
                    }
                }

                private static void MayThrow()
                {
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CatchHandlers_ObserveAThrownTransfer()
    {
        // A thrown transfer lands in the try's handlers: the catch runs after the throw
        // expression evaluated, so its Add touches the handed-off list.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public sealed class E : Exception
            {
                public E(Sending<List<int>> payload)
                {
                }
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        throw new E(Sending.Transfer(list));
                    }
                    catch
                    {
                        list.Add(1);
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task TupleElements_HandedToTransfer_AreTracked()
    {
        // Transfer((list, 0)): the tuple carries the same list reference to the receiver.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<(List<int>, int)> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer((list, 0));
                    list.Add(1);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task SwitchExpressionArms_HandedToTransfer_AreTracked()
    {
        // One arm hands off `list`: a may-transfer, tracked like a ternary operand.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M(bool flag)
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(flag switch { true => list, _ => new List<int>() });
                    list.Add(1);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task NameOfMentions_AreNotRuntimeUses()
    {
        // nameof(list) is a compile-time constant: no runtime read of the transferred object
        // occurs, so it must not block the handoff.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    _ = nameof(list);
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UsingStatements_OverExistingLocals_DisposeAfterTheTransfer()
    {
        // `using (stream)` over an existing local emits the same scope-end Dispose as a
        // using declaration: the sender destroys the object the receiver now owns.
        var diagnostics = await AnalyzeAsync("""
            using System.IO;
            using MemoizR;

            public class C
            {
                public Sending<MemoryStream> M(MemoryStream stream)
                {
                    using (stream)
                    {
                        return Sending.Transfer(stream);
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'stream'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task LocalFunctionDeclarations_DoNotMakeCatchesReachable()
    {
        // The declaration after the transfer neither executes nor throws on the try path: the
        // catch still cannot observe a completed handoff.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        _ = Sending.Transfer(list);
                        void Helper()
                        {
                        }
                    }
                    catch
                    {
                        list.Add(1);
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UsingLocals_AreDisposedBySenderAfterTheTransfer()
    {
        // The scope's compiler-generated Dispose runs after the handoff with no source
        // reference to scan for: the sender destroys the object the receiver now owns.
        var diagnostics = await AnalyzeAsync("""
            using System.IO;
            using MemoizR;

            public class C
            {
                public Sending<MemoryStream> M()
                {
                    using var stream = new MemoryStream();
                    var sent = Sending.Transfer(stream);
                    return sent;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'stream'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task FinallyBlocks_RunAfterAReturnTransfer_AndAreStillScanned()
    {
        // `return Sending.Transfer(list)` exits the method -- but the enclosing finally runs
        // AFTER the return expression evaluated, so its Add touches the transferred value.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        return Sending.Transfer(list);
                    }
                    finally
                    {
                        list.Add(1);
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InlineAssignmentTransfer_IsTracked()
    {
        // Transfer(list = new(...)): after the statement the variable aliases the transferred
        // value, so the later Add is a use-after-transfer like any other.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    List<int> list;
                    var sending = Sending.Transfer(list = new List<int> { 1 });
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
    public async Task DeconstructionTarget_Reinitializes_AndEndsTracking()
    {
        // A deconstruction target is definitely assigned like a simple-assignment target: the
        // later use touches the fresh value.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    (list, _) = (new List<int>(), 0);
                    list.Add(1);
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DeconstructionRhsReadingTheTransferredValue_IsFlagged()
    {
        // Same caveat as `list = Clone(list)`: the deconstruction's RHS reads the transferred
        // value to build the replacement -- exactly a use after transfer.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    (list, _) = (new List<int>(list), 0);
                    return sending;
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
