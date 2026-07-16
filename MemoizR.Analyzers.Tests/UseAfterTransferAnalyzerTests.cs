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
    public async Task TransferAndOutReinitialization_InOneCall_IsClean()
    {
        // The reference inside the transfer argument IS the handoff, not a post-transfer
        // sibling read: the out assignment then hands the later Add a fresh list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    Reset(Sending.Transfer(list), out list);
                    list.Add(1);
                }

                private static void Reset(Sending<List<int>> sending, out List<int> value)
                {
                    value = new List<int>();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UsingResources_MerelyMentioningTheVariable_DoNotDisposeIt()
    {
        // The using disposes the Scope wrapper, not the transferred list: a resource that
        // only MENTIONS the variable is no disposal of the handoff.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public sealed class Scope : IDisposable
            {
                public Scope(int size)
                {
                }

                public void Dispose()
                {
                }
            }

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    using (new Scope(list.Count))
                    {
                        return Sending.Transfer(list);
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ThrowingReinitializers_KeepEnclosingCatchesAlive()
    {
        // Throwing() can throw AFTER the handoff and BEFORE the assignment completes: the
        // catch still sees the transferred list on that path.
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
                        var sent = Sending.Transfer(list);
                        list = Throwing();
                    }
                    catch
                    {
                        list.Add(1);
                    }
                }

                private static List<int> Throwing()
                {
                    return new List<int>();
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ForeachIteration_ReadsTheCollectionAfterATransferInsideTheLoop()
    {
        // The next MoveNext reads the transferred list -- a sender-side use with no source
        // reference to scan for.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    foreach (var item in list)
                    {
                        _ = Sending.Transfer(list);
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ForeachTransfer_FollowedByADefiniteBreak_IsClean()
    {
        // The break on the transfer's own conditional level leaves the loop before any
        // further MoveNext: the handoff is the find-and-transfer pattern.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    foreach (var item in list)
                    {
                        _ = Sending.Transfer(list);
                        break;
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task EnclosingCalls_CanThrowAfterTheWrapperExists_AndReachTheCatch()
    {
        // MayThrow runs AFTER building the wrapper and may throw with the handoff complete:
        // the catch use is reachable.
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
                        MayThrow(Sending.Transfer(list));
                    }
                    catch
                    {
                        list.Add(1);
                    }
                }

                private static void MayThrow(Sending<List<int>> sending)
                {
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task AssignmentRhsAliases_AreTrackedToo()
    {
        // Transfer(list = other): BOTH names alias the handed-off object, so the later use of
        // the original RHS alias is a use-after-transfer.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M(List<int> other)
                {
                    List<int> list;
                    var sending = Sending.Transfer(list = other);
                    other.Add(1);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'other'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ThrowingReinitializers_KeepEnclosingFinallysAliveToo()
    {
        // The finally runs whether or not Throwing() threw before the assignment completed:
        // on the throwing path it observes the transferred value.
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
                        var sent = Sending.Transfer(list);
                        list = Throwing();
                    }
                    finally
                    {
                        list.Add(1);
                    }
                }

                private static List<int> Throwing()
                {
                    return new List<int>();
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ThrowingGetters_OpenTheReinitializationWindowToo()
    {
        // A property getter is a method: it can throw after the handoff, before the target is
        // assigned, so the catch still sees the transferred value.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public static List<int> Next => new();

                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sent = Sending.Transfer(list);
                        list = Next;
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
    public async Task ReturningCalls_ThatCanThrowAfterTheTransfer_ReachTheCatch()
    {
        // The returning call can throw after the wrapper exists, before the method exits: the
        // local catch runs with the handoff complete.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>>? M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        return MayThrow(Sending.Transfer(list));
                    }
                    catch
                    {
                        list.Add(1);
                        return null;
                    }
                }

                private static Sending<List<int>> MayThrow(Sending<List<int>> sending)
                {
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task LocalFunctionExits_DoNotEndTheForeachIteration()
    {
        // The nested return never runs on the loop path: the next MoveNext still reads the
        // transferred collection.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    foreach (var item in list)
                    {
                        _ = Sending.Transfer(list);
                        void F()
                        {
                            return;
                        }
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task SwitchBreaks_DoNotEndTheForeachIteration()
    {
        // The break leaves the switch, not the foreach: the next MoveNext still reads the
        // transferred collection.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(int n)
                {
                    var list = new List<int> { 1 };
                    foreach (var item in list)
                    {
                        switch (n)
                        {
                            case 0:
                                _ = Sending.Transfer(list);
                                break;
                        }
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task LocalFunctionCalls_AfterTheTransfer_AreUses()
    {
        // The read inside Use sits source-BEFORE the transfer, but the CALL runs after it:
        // the sender still touches the handed-off list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    void Use() => list.Add(1);
                    var sending = Sending.Transfer(list);
                    Use();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task LockScopes_ExitOnTheTransferredObject()
    {
        // The compiler-generated Monitor.Exit touches the handed-off object at scope end:
        // same shape as using disposal.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var gate = new List<int> { 1 };
                    lock (gate)
                    {
                        return Sending.Transfer(gate);
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'gate'", diagnostic.GetMessage());
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

    [Fact]
    public async Task UncalledLocalFunctionBodies_AreNotUses()
    {
        // Declaring a local function executes nothing: with no call, the body's reference
        // never runs.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Unused()
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
    public async Task LocalFunctionsThatReinitializeBeforeReading_AreNotUses()
    {
        // Calling Reset only ever touches the fresh value: the body reassigns the variable
        // before any read, so the call is a reinitialization, not a use.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Reset()
                    {
                        list = new List<int>();
                        list.Add(1);
                    }
                    Reset();
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ReinitializingAssignmentEnclosingTheTransfer_EndsTracking()
    {
        // The assignment wrapping the transfer completes right after its RHS: by the next
        // statement the variable already holds the fresh value.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    list = MakeFresh(Sending.Transfer(list));
                    list.Add(1);
                }

                private static List<int> MakeFresh(Sending<List<int>> sending) => new();
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ReadsInsideTheEnclosingReinitializationRhs_AreStillUses()
    {
        // The reinitialization only lands AFTER the whole RHS runs: a sibling argument
        // evaluated after the transfer still reads the handed-off value.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    list = Combine(Sending.Transfer(list), list.Count);
                }

                private static List<int> Combine(Sending<List<int>> sending, int count) => new();
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ConditionalReinitializersInLocalFunctions_DoNotEndTracking()
    {
        // Use(false) skips the reset and reads the transferred list: only a reassignment
        // that DEFINITELY runs before any read makes the call safe.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Use(bool reset)
                    {
                        if (reset) list = new List<int>();
                        list.Add(1);
                    }
                    Use(false);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ReadsDominatedByALocalFunctionsConditionalReset_AreFresh()
    {
        // Every path through the call either resets before the read or skips both: the
        // transferred value is never touched.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Use(bool reset)
                    {
                        if (reset)
                        {
                            list = new List<int>();
                            list.Add(1);
                        }
                    }
                    Use(false);
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task YieldReturnTransfers_KeepTheSenderScanAlive()
    {
        // yield return hands the wrapper to the caller and RESUMES the same body on the next
        // MoveNext: the later Add is ordinary sender-side use, not code past a method exit.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public IEnumerable<Sending<List<int>>> M()
                {
                    var list = new List<int> { 1 };
                    yield return Sending.Transfer(list);
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task YieldReturns_DoNotEndTheForeachIteration()
    {
        // The iterator resumes INSIDE the loop on the next MoveNext: the foreach still reads
        // the transferred collection.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public IEnumerable<int> M()
                {
                    var list = new List<int> { 1 };
                    foreach (var item in list)
                    {
                        _ = Sending.Transfer(list);
                        yield return item;
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ResettingLocalFunctionCalls_AreReinitializers()
    {
        // Reset() rewrites the variable on every path through it: after the call the later
        // Add only ever touches the fresh value.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Reset()
                    {
                        list = new List<int>();
                    }
                    Reset();
                    list.Add(1);
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ConditionallyCalledResets_DoNotSilenceTheLaterUse()
    {
        // The resetting call may be skipped: the path around it still reads the transferred
        // value at the Add.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M(bool keep)
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Reset()
                    {
                        list = new List<int>();
                    }
                    if (!keep) Reset();
                    list.Add(1);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task HandlerResets_BeforeAnyRead_KeepTheCatchClean()
    {
        // The recovery path overwrites the variable before touching it: the catch never
        // reads the transferred object, however the reinitializing RHS throws.
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
                        list = MayThrow();
                    }
                    catch
                    {
                        list = new List<int>();
                        list.Add(1);
                    }
                }

                private static List<int> MayThrow() => new();
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task HandlerRhsReads_AreStillWindowUses()
    {
        // The handler's replacement is BUILT FROM the transferred value: that read runs
        // exactly in the window where the receiver may already own it.
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
                        list = MayThrow();
                    }
                    catch
                    {
                        list = new List<int>(list);
                    }
                }

                private static List<int> MayThrow() => new();
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task LocalFunctionResets_InsideATry_KeepTheCatchWindowVisible()
    {
        // The body's reset can throw before storing: its catch reads the transferred value,
        // so calling the function is a use -- the try-nested reset is CONDITIONAL, not a
        // definite rewrite.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Call()
                    {
                        try
                        {
                            list = MayThrow();
                        }
                        catch
                        {
                            list.Add(1);
                        }
                    }
                    Call();
                    return sending;
                }

                private static List<int> MayThrow() => new();
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task UncalledNestedDeclarations_DoNotMakeTheCallARead()
    {
        // F() runs nothing: the only reference lives in a nested declaration F never
        // invokes, and declarations do not execute.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void F()
                    {
                        void Unused()
                        {
                            list.Add(1);
                        }
                    }
                    F();
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task WrapperCalls_ThatReachAReadingFunction_StillReport()
    {
        // Outer() -> Use() -> the read runs after the handoff: the chained call is a use.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Outer()
                    {
                        Use();
                    }
                    void Use()
                    {
                        list.Add(1);
                    }
                    Outer();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CallsInsideUncalledLocalFunctions_AreNotUses()
    {
        // Use() is only invoked from Outer's body, and Outer never runs: neither the
        // declaration nor the call inside it executes.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Outer()
                    {
                        Use();
                    }
                    void Use()
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
    public async Task GeneratedDisposes_TrackTheCapturedObject_NotTheReassignedVariable()
    {
        // The using captured the ORIGINAL stream at entry: after the definite reassignment,
        // scope-end Dispose touches the old object, not the one handed off.
        var diagnostics = await AnalyzeAsync("""
            using System.IO;
            using MemoizR;

            public class C
            {
                public Sending<MemoryStream> M()
                {
                    var stream = new MemoryStream();
                    using (stream)
                    {
                        stream = new MemoryStream();
                        return Sending.Transfer(stream);
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ForeachIteration_AfterADefiniteReassignment_ReadsADifferentList()
    {
        // The enumerator iterates the collection captured at loop entry: every iteration
        // reassigns before transferring, so MoveNext never touches the handed-off object.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    foreach (var item in list)
                    {
                        list = new List<int>();
                        _ = Sending.Transfer(list);
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ArgumentsOfResettingCalls_AreEvaluatedBeforeTheReset()
    {
        // Reset(list.Count): the argument reads the transferred value BEFORE the body's
        // reset runs -- the reset cannot retroactively excuse it.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Reset(int unused)
                    {
                        list = new List<int>();
                    }
                    Reset(list.Count);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task NonThrowingCodeAfterTheTransfer_DoesNotKeepCatchesAlive()
    {
        // Nothing after the completed handoff can throw: the handler can never observe the
        // wrapper, so its read is unreachable on every transferred path.
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
                        var sent = Sending.Transfer(list);
                        var x = 0;
                        _ = x;
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
    public async Task OutResets_InLocalFunctionBodies_AreReinitializers()
    {
        // An out parameter must be assigned before Init returns: calling Reset only ever
        // rewrites the variable, exactly like a simple assignment.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Reset()
                    {
                        Init(out list);
                    }
                    Reset();
                    list.Add(1);
                    return sending;
                }

                private static void Init(out List<int> target) => target = new List<int>();
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DeconstructionResets_InLocalFunctionBodies_AreReinitializers()
    {
        // A deconstruction target is definitely assigned like a simple-assignment target --
        // inside a called body too.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    void Reset()
                    {
                        (list, _) = (new List<int>(), 0);
                    }
                    Reset();
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DelegateCalls_AfterTheTransfer_AreUses()
    {
        // The lambda body sits source-BEFORE the transfer, but invoking the delegate runs it
        // after: the sender still touches the handed-off list.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    use();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task SiblingLocalFunctionChains_DeclaredBeforeTheTransfer_StillReport()
    {
        // Outer() -> Use() -> the read: both declarations sit before the handoff, but the
        // post-transfer call still reaches the transferred value through the chain.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    void Outer()
                    {
                        Use();
                    }
                    void Use()
                    {
                        list.Add(1);
                    }
                    var sending = Sending.Transfer(list);
                    Outer();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CallsThatOnlyRanBeforeTheTransfer_AreNotUses()
    {
        // Outer() ran before the handoff: nothing in its declaration executes afterwards,
        // wherever the declaration happens to sit in source.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Outer();
                    var sending = Sending.Transfer(list);
                    void Outer()
                    {
                        Use();
                    }
                    void Use()
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
    public async Task CollectionExpressionElements_AreTransferSources()
    {
        // Transfer([list]) hands off a collection carrying the same element reference,
        // exactly like an explicit array creation.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer<List<int>[]>([list]);
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ConditionalDelegateInvokes_AreStillUses()
    {
        // use?.Invoke() reaches the same lambda through a conditional-access receiver: the
        // wrapper around the local must not hide the call.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Action? use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    use?.Invoke();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task SiblingArmDelegateStores_CannotReachTheCall()
    {
        // The reading lambda is stored only in the arm the transfer path never runs: the
        // call in the transfer arm can only see the earlier no-op.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool move)
                {
                    var list = new List<int> { 1 };
                    Action use = () => { };
                    if (move)
                    {
                        _ = Sending.Transfer(list);
                        use();
                    }
                    else
                    {
                        use = () => list.Add(1);
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DelegateStores_AfterTheCall_CannotBeItsValue()
    {
        // The reading lambda lands in the local only after the call already ran the earlier
        // no-op: the CALL is not a use. What remains is the standing escape stance on the
        // post-transfer lambda itself -- so the report anchors at the lambda's read, not at
        // use().
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => { };
                    var sending = Sending.Transfer(list);
                    use();
                    use = () => list.Add(1);
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Equal("list", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public async Task SpreadsOfInlineContainers_CarryTheirElements()
    {
        // [.. new[] { list }] enumerates the inline array INTO the new collection: the same
        // list reference arrives at the receiver.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer<List<int>[]>([.. new[] { list }]);
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SpreadsOfPlainVariables_DoNotHandOffTheOperand()
    {
        // [..source] copies source's ELEMENTS into the new collection: the source object
        // itself stays with the sender.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var source = new List<int> { 1 };
                    var sending = Sending.Transfer<int[]>([.. source]);
                    source.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CaughtThrows_DoNotEndTheForeachIteration()
    {
        // The throw is absorbed inside the loop body: control stays in the foreach and the
        // next MoveNext still reads the transferred collection.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    foreach (var item in list)
                    {
                        try
                        {
                            _ = Sending.Transfer(list);
                            throw new System.Exception();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task LocallyCaughtThrownTransfers_KeepTheSenderScanAlive()
    {
        // The matching catch absorbs the throw: the method resumes after the try, where the
        // sender still touches the handed-off list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class PayloadException : System.Exception
            {
                public PayloadException(Sending<List<int>> payload)
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
                        throw new PayloadException(Sending.Transfer(list));
                    }
                    catch (PayloadException)
                    {
                    }

                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ArgumentResets_GiveTheBodyAFreshValue()
    {
        // The argument reassigns BEFORE the body runs: the captured read inside Use only
        // ever sees the fresh list, and so does everything after the call.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    void Use(object unused) => list.Add(1);
                    var sending = Sending.Transfer(list);
                    Use(list = new List<int>());
                    list.Add(2);
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ArgumentResetsBuiltFromTheValue_AreStillUses()
    {
        // The replacement is built FROM the transferred value on the way into the call.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    void Use(object unused) => list.Add(1);
                    var sending = Sending.Transfer(list);
                    Use(list = new List<int>(list));
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CatchlessTryResets_AreDefinite()
    {
        // The try body is entered unconditionally and nothing can resume past a failed
        // reset: reaching the Add means the assignment completed.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    try
                    {
                        list = new List<int>();
                    }
                    finally
                    {
                    }

                    list.Add(1);
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task TryResetsWithCatches_StayConditional()
    {
        // A catch can resume past the failed reset: the Add may still see the transferred
        // value on that path.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(list);
                    try
                    {
                        list = MayThrow();
                    }
                    catch
                    {
                    }

                    list.Add(1);
                    return sending;
                }

                private static List<int> MayThrow() => new();
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CallablesInsideEscapingExpressions_ResolveAgainstTheWholeBody()
    {
        // The escaping expression evaluates Use() after the handoff, before the method
        // exits -- and Use's declaration sits OUTSIDE the return expression, so the lookup
        // must reach the whole body.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    int Use()
                    {
                        list.Add(1);
                        return 0;
                    }
                    return Pair(Sending.Transfer(list), Use());
                }

                private static Sending<List<int>> Pair(Sending<List<int>> sending, int x) => sending;
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ObjectInitializerFieldStores_AreTransferSources()
    {
        // Box.Value is a FIELD: the initializer deterministically stores the same list into
        // the object the receiver now owns.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Box
            {
                public List<int>? Value;
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box { Value = list });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ObjectInitializerAutoPropertyStores_AreTransferSources()
    {
        // An auto-property is a compiler-known slot: the initializer's store is as
        // deterministic as a field write.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Box
            {
                public List<int>? Value { get; set; }
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box { Value = list });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ObjectInitializerCustomSetters_AreNotDeterministicStores()
    {
        // A custom setter may not store what it was handed: only compiler-known slots count
        // as carrying the reference to the receiver.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Sink
            {
                public List<int>? Value
                {
                    get => null;
                    set { }
                }
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Sink { Value = list });
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FieldReads_AreThrowCapable_AndKeepCatchesAlive()
    {
        // Reading an instance field off a possibly-null receiver throws after the completed
        // handoff: the catch observes the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Holder
            {
                public int Count;
            }

            public class C
            {
                public void M(Holder holder)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sent = Sending.Transfer(list);
                        _ = holder.Count;
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
    public async Task CollectionInitializerElements_AreTransferSources()
    {
        // new List<List<int>> { list } stores the element exactly like a collection
        // expression: the receiver owns a collection containing the same reference.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new List<List<int>> { list });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task CaughtReturnExpressionFailures_KeepTheSenderScanAlive()
    {
        // MayThrow can fail AFTER the wrapper exists, landing in the catch-all: control then
        // resumes after the try and the sender still touches the handed-off list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>>? M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        return MayThrow(Sending.Transfer(list));
                    }
                    catch
                    {
                    }

                    list.Add(1);
                    return null;
                }

                private static Sending<List<int>> MayThrow(Sending<List<int>> sending) => sending;
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CaughtThrowsBeforeResets_KeepThePathOpen()
    {
        // On the throwing path the reset never runs, the catch-all resumes, and the final
        // Add still touches the transferred list: the reset is not definite.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool fail)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        _ = Sending.Transfer(list);
                        if (fail) throw new System.Exception();
                        list = new List<int>();
                    }
                    catch
                    {
                    }

                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task DelegateCallsInsideCalledFunctions_AreFollowed()
    {
        // F() runs the delegate, and the delegate runs the reading lambda: the chain
        // reaches the transferred value transitively.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => list.Add(1);
                    void F()
                    {
                        use();
                    }
                    var sending = Sending.Transfer(list);
                    F();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task IndexerInitializerValues_AreTransferSources()
    {
        // ["x"] = list stores the same reference into the dictionary the receiver now owns.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Dictionary<string, List<int>> { ["x"] = list });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task GenericLocalFunctionCalls_ResolveTheirDeclarations()
    {
        // Use<int>() carries a constructed symbol; the declaration carries the definition --
        // the lookup must still connect them.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    void Use<T>()
                    {
                        list.Add(1);
                    }
                    var sending = Sending.Transfer(list);
                    Use<int>();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CombinedDelegateStores_AreCallablesToo()
    {
        // += multicasts the reading lambda onto the delegate: invoking it after the handoff
        // runs that lambda.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => { };
                    use += () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    use();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task LocalReturnsBeforeResets_KeepTheResetConditional()
    {
        // Reset(true) returns before the assignment: the call completes without rewriting,
        // and the later Add still touches the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Reset(bool skip)
                    {
                        if (skip) return;
                        list = new List<int>();
                    }
                    _ = Sending.Transfer(list);
                    Reset(true);
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task OverwrittenDelegateTargets_CannotBeTheCallsValue()
    {
        // The definite overwrite kills the reading initializer before the handoff: only the
        // no-op can run at the call.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => list.Add(1);
                    use = () => { };
                    var sending = Sending.Transfer(list);
                    use();
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DelegateRemovals_StoreNothing()
    {
        // -= removes from the invocation list; the lambda on its right side never becomes a
        // target of the delegate.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => { };
                    use -= () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    use();
                    return sending;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DelegateAliases_ResolveToTheirStoredCallables()
    {
        // use holds what use2 held: the reading lambda runs at the call through the alias.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Sending<List<int>> M()
                {
                    var list = new List<int> { 1 };
                    Action use2 = () => list.Add(1);
                    Action use = use2;
                    var sending = Sending.Transfer(list);
                    use();
                    return sending;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ThrowingConversions_KeepCatchesAlive()
    {
        // The user-defined conversion can throw after the completed handoff: the catch then
        // observes the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Source
            {
                public static explicit operator Target(Source source) => new Target();
            }

            public class Target
            {
            }

            public class C
            {
                public void M(Source source)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sent = Sending.Transfer(list);
                        _ = (Target)source;
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
    public async Task FieldReadsOnThis_CannotThrow_AndFreeTheCatch()
    {
        // Reading a field through `this` has no exception path: nothing after the completed
        // handoff can carry the wrapper into the handler.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private int count;

                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sent = Sending.Transfer(list);
                        _ = count;
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
    public async Task EnclosingDeconstructionResets_EndTracking()
    {
        // The deconstruction assigns the fresh list right after its RHS evaluated: by the
        // Add the transferred value is gone from the variable.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    (list, _) = (new List<int>(), Sending.Transfer(list));
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task TransfersInsideCalledFunctions_ReachTheCallersContinuation()
    {
        // Move() performs the handoff before returning: the caller's Add is sequenced after
        // it exactly like an inline transfer.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Move()
                    {
                        _ = Sending.Transfer(list);
                    }
                    Move();
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ParameterTransfers_RemapToTheCallersArgument()
    {
        // Move(list) hands the caller's list to the parameter the body transfers: at the
        // call site the tracked variable is the argument, not the callee's alias.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Move(List<int> x)
                    {
                        _ = Sending.Transfer(x);
                    }
                    Move(list);
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DeconstructionsWritingTheValueBack_DoNotReset()
    {
        // The matched RHS element IS the old list: the deconstruction writes the transferred
        // reference back, so the later Add still touches it.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    (list, _) = (list, Sending.Transfer(list));
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task WithExpressionInitializers_AreTransferSources()
    {
        // The clone the receiver owns carries the same list in its Value slot.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public record Box
            {
                public List<int>? Value { get; init; }
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var box = new Box();
                    var sending = Sending.Transfer(box with { Value = list });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task TransfersPropagate_ThroughDelegateCarriedFunctions()
    {
        // move() invokes Move through the delegate: the handoff inside it is sequenced
        // before the caller's Add exactly like a direct call.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Move()
                    {
                        _ = Sending.Transfer(list);
                    }
                    Action move = Move;
                    move();
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ExplicitThrows_KeepCatchesAlive()
    {
        // Rethrowing an existing exception allocates nothing, but still runs after the
        // completed handoff and lands in the handler, which then reads the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(System.Exception ex)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sent = Sending.Transfer(list);
                        throw ex;
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
    public async Task CallsWhoseArgumentsContainTheTransfer_RunTheirBodyAfterIt()
    {
        // The callee body runs only after all arguments evaluated -- the handoff included.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Use(Sending<List<int>> unused)
                    {
                        list.Add(1);
                    }
                    Use(Sending.Transfer(list));
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task DelegateStoresInsideUncalledFunctions_NeverRan()
    {
        // Configure is never invoked: its store cannot be the delegate's value at the call.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => { };
                    void Configure()
                    {
                        use = () => list.Add(1);
                    }
                    var sending = Sending.Transfer(list);
                    use();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DelegateAliases_SnapshotTheSourceAtTheCopy()
    {
        // use copied src BEFORE the reading lambda landed in src: the copy keeps the old
        // invocation list, so the call runs only the no-op.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    Action src = () => { };
                    Action use = src;
                    src = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    use();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ThrowingCallsBeforeResets_KeepThePathOpen()
    {
        // The caught MayThrow() path skips the reset and resumes after the try: the final
        // Add still touches the transferred list on that path.
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
                        list = new List<int>();
                    }
                    catch
                    {
                    }

                    list.Add(1);
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
    public async Task RecordConstructorArguments_AreTransferSources()
    {
        // A positional record's primary constructor deterministically stores each parameter
        // in its same-named slot: the receiver owns a Box carrying the same list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public record Box(List<int> Value);

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box(list));
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task UserDefinedInitializerAdds_AreNotKnownStores()
    {
        // Sink.Add ignores its argument: nothing moved to the receiver, so the later Add on
        // the list is sender-local.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Sink : System.Collections.IEnumerable
            {
                public void Add(List<int> item)
                {
                }

                public System.Collections.IEnumerator GetEnumerator() => throw new System.NotImplementedException();
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Sink { list });
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UserDefinedIndexerInitializers_AreNotKnownStores()
    {
        // A custom indexer setter may drop what it was handed: only framework collections
        // count as deterministic stores.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Sink
            {
                public List<int>? this[int index]
                {
                    get => null;
                    set { }
                }
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Sink { [0] = list });
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task LocalFunctionMethodGroups_EscapeLikeLambdas()
    {
        // Returning Use hands the caller a delegate over the retained alias: it can run
        // after the method exits, exactly like the equivalent lambda.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Action M()
                {
                    var list = new List<int> { 1 };
                    _ = Sending.Transfer(list);
                    return Use;

                    void Use()
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
    public async Task SynthesizedTransferCalls_AreNotTheirOwnUses()
    {
        // Move() IS the propagated handoff: with nothing after it, the sender never touches
        // the list again.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Move()
                    {
                        _ = Sending.Transfer(list);
                    }
                    Move();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ParametersReassignedBeforeTheTransfer_NoLongerAliasTheArgument()
    {
        // Move transfers its own fresh list: the caller's original was never handed off.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Move(List<int> x)
                    {
                        x = new List<int>();
                        _ = Sending.Transfer(x);
                    }
                    Move(list);
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task StaticFieldReads_MayRunTypeInitializers_AndKeepCatchesAlive()
    {
        // The first touch of Holder can run its type initializer, which may throw after the
        // completed handoff: the catch then observes the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public static class Holder
            {
                public static int Value;
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sent = Sending.Transfer(list);
                        _ = Holder.Value;
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
    public async Task NonResumingCatches_CannotSkipResets()
    {
        // The handler rethrows: either the reset ran, or the method exited -- no path
        // reaches the Add with the old list.
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
                        list = new List<int>();
                    }
                    catch
                    {
                        throw;
                    }

                    list.Add(1);
                }

                private static void MayThrow()
                {
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DynamicOperations_AreThrowCapable()
    {
        // A dynamic dispatch can fail at runtime after the completed handoff: the catch
        // then observes the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(dynamic dyn)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sent = Sending.Transfer(list);
                        dyn.Foo();
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
    public async Task CustomRecordConstructors_DoNotCarryByNameAlone()
    {
        // The hand-written constructor ignores its parameter: a matching name proves no
        // storage -- only the PRIMARY constructor's parameters deterministically store.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public record Box
            {
                public List<int>? Value { get; init; }

                public Box(List<int> Value)
                {
                }
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box(list));
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NestedMemberInitializers_AreTransferSources()
    {
        // Child = { Value = list } writes into the object the transferred Box already
        // reaches: the receiver owns a graph containing the same list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Inner
            {
                public List<int>? Value { get; set; }
            }

            public class Box
            {
                public Inner Child { get; } = new Inner();
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box { Child = { Value = list } });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task WithExpressionOperands_CarryTheirInlineContents()
    {
        // The clone copies Value from the inline operand before the initializer applies:
        // the receiver's Box carries the same list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public record Box(List<int> Value)
            {
                public int Other { get; init; }
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box(list) with { Other = 1 });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task TransfersInsideInvokedLambdas_ReachTheCallersContinuation()
    {
        // move() runs the lambda that performs the handoff: the caller's Add is sequenced
        // after it exactly like a local-function call.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    Func<Sending<List<int>>> move = () => Sending.Transfer(list);
                    var sent = move();
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task NestedInitializers_OnComputedMembers_AreNotRetained()
    {
        // Child hands out a fresh temporary: the write lands in an object the transferred
        // Box never retains.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class Inner
            {
                public List<int>? Value { get; set; }
            }

            public class Box
            {
                public Inner Child => new Inner();
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box { Child = { Value = list } });
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AliasPreservingParameterAssignments_KeepThePropagation()
    {
        // x = x rewrites nothing: the parameter still aliases the caller's list when the
        // body transfers it.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Move(List<int> x)
                    {
                        x = x;
                        _ = Sending.Transfer(x);
                    }
                    Move(list);
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task StoredDelegateEscapes_AfterTheTransfer_AreUses()
    {
        // The returned delegate captures the transferred list: the caller can run it
        // against the receiver-owned object at any time.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Action M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    return use;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task FrameworkCollectionsOutsideCorelib_StillStore()
    {
        // BlockingCollection lives in System.Collections.Concurrent, not the core library:
        // its Add still deterministically stores the element.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new BlockingCollection<List<int>> { list });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task FinallyBlocks_RunBeforeAFailedReset_AndSeeTheTransferredValue()
    {
        // A failing MayThrow() runs the finally while the variable still holds the
        // transferred object.
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
                        list = new List<int>();
                    }
                    finally
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
    public async Task RedeclaredPositionalProperties_DropTheParameter()
    {
        // The explicit Value property has its own initializer: the primary parameter is
        // never stored, so the receiver's Box does not carry the list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public record Box(List<int> Value)
            {
                public List<int> Value { get; init; } = new List<int>();
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box(list));
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InspectedDelegates_DoNotEscape()
    {
        // A null check neither runs the delegate nor lets it leave the scope.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    if (use != null)
                    {
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task OutResetsBeforeTheTransfer_BreakTheParameterAlias()
    {
        // Fresh(out x) rewrites the parameter before the handoff: the callee transferred
        // its own fresh value, not the caller's list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Move(List<int> x)
                    {
                        Fresh(out x);
                        _ = Sending.Transfer(x);
                    }
                    Move(list);
                    list.Add(1);
                }

                private static void Fresh(out List<int> target) => target = new List<int>();
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UnmatchedThrowTypes_CannotSkipResets()
    {
        // The catch cannot catch InvalidOperationException: the throwing path exits the
        // method, so every path reaching the Add ran the reset.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool bad)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        _ = Sending.Transfer(list);
                        if (bad) throw new System.InvalidOperationException();
                        list = new List<int>();
                    }
                    catch (System.ArgumentException)
                    {
                    }

                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task OutArguments_DoNotMaskReadingBodies()
    {
        // The out write lands only when Reset returns -- AFTER its body read the
        // transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Reset(out List<int> fresh)
                    {
                        list.Add(1);
                        fresh = new List<int>();
                    }
                    _ = Sending.Transfer(list);
                    Reset(out list);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ThrowCapableSiblingArms_DoNotReachTheCatch()
    {
        // MayThrow lives in the arm the transfer path never runs: no exception can carry
        // the wrapper into the handler.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool move)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        if (move)
                        {
                            _ = Sending.Transfer(list);
                        }
                        else
                        {
                            MayThrow();
                        }
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

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task WrappingCalls_CanThrowBeforeTheReset_AndKeepThePathOpen()
    {
        // MayThrow can fail after the wrapper exists and before the reset: the caught path
        // resumes with the transferred list still in the variable.
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
                        MayThrow(Sending.Transfer(list));
                        list = new List<int>();
                    }
                    catch
                    {
                    }

                    list.Add(1);
                }

                private static void MayThrow(Sending<List<int>> sending)
                {
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task RefParameterTransfers_StayMappedToTheCaller()
    {
        // x writes through to the caller's slot: the fresh value was transferred AND lives
        // in list, so the caller's Add touches the handed-off object.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Move(ref List<int> x)
                    {
                        x = new List<int>();
                        _ = Sending.Transfer(x);
                    }
                    Move(ref list);
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task SendableTupleElements_AreNotTracked()
    {
        // The tuple copies id by value and int is deeply immutable: only the list can alias
        // mutable state with the receiver, so only its use reports.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var id = 1;
                    var list = new List<int> { 1 };
                    var sent = Sending.Transfer((id, list));
                    _ = id;
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task EventAccessors_AreThrowCapable()
    {
        // A custom add accessor is user code: it can throw after the completed handoff and
        // land in the catch, which reads the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public event Action? Changed;

                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sent = Sending.Transfer(list);
                        Changed += Handler;
                    }
                    catch
                    {
                        list.Add(1);
                    }
                }

                private static void Handler()
                {
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task DefinitelyRemovedTargets_CannotRun()
    {
        // use -= Touch empties the invocation list before the handoff: the null-conditional
        // call runs nothing.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Touch() => list.Add(1);
                    Action? use = Touch;
                    use -= Touch;
                    var sending = Sending.Transfer(list);
                    use?.Invoke();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UsingDisposal_IsThrowCapable()
    {
        // The scope's Dispose runs after the handoff and can throw into the catch, which
        // then reads the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.IO;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        using (var scope = new MemoryStream())
                        {
                            _ = Sending.Transfer(list);
                        }
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
    public async Task UnconstrainedGenericSources_AreTracked()
    {
        // T can be any mutable type at the call site: the generic helper's post-transfer
        // read is exactly the hazard MZR005 exists for.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M<T>(T value)
                {
                    _ = Sending.Transfer(value);
                    _ = value;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task StructFieldReads_CannotThrow()
    {
        // Reading a field off a struct local dereferences nothing: no exception path can
        // carry the wrapper into the handler.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public struct Point
            {
                public int X;
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var point = new Point();
                    try
                    {
                        var sent = Sending.Transfer(list);
                        _ = point.X;
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
    public async Task SiblingArmDelegateStores_CannotEscapeOnTheTransferPath()
    {
        // The reading lambda lands in use only on the arm that did NOT transfer: no path
        // both hands off the list and returns that callable.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Action? M(bool move)
                {
                    var list = new List<int> { 1 };
                    Action? use = null;
                    if (move)
                    {
                        _ = Sending.Transfer(list);
                    }
                    else
                    {
                        use = () => list.Add(1);
                    }

                    return use;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DuplicateMulticastTargets_SurviveOneRemoval()
    {
        // Touch was added twice and removed once: one target remains and reads the
        // transferred list at the call.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Touch() => list.Add(1);
                    Action? use = Touch;
                    use += Touch;
                    use -= Touch;
                    var sending = Sending.Transfer(list);
                    use?.Invoke();
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task DelegateParameters_EscapeLikeLocals()
    {
        // The parameter holds the reading lambda and is returned after the handoff: the
        // caller can run it against the receiver-owned object.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Action M(Action use)
                {
                    var list = new List<int> { 1 };
                    use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    return use;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task FrameworkValueCarrierConstructors_CarryTheirArguments()
    {
        // KeyValuePair's constructor deterministically stores both arguments: the receiver
        // owns a pair containing the same list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new KeyValuePair<string, List<int>>("k", list));
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SendableArguments_AreNotRemappedAtCallSites()
    {
        // Move receives a boxed immutable int: nothing mutable reached the receiver, so
        // the caller's later read is harmless.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M()
                {
                    void Move(object x)
                    {
                        _ = Sending.Transfer(x);
                    }
                    var id = 1;
                    Move(id);
                    _ = id;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task LeafWithOperands_ShareTheirSlots()
    {
        // The clone shallow-copies box's Value: the receiver-owned clone and the sender's
        // box now share the same list, so mutating through box is a use.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public record Box(List<int> Value)
            {
                public int Other { get; init; }
            }

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var box = new Box(list);
                    var sending = Sending.Transfer(box with { Other = 1 });
                    box.Value.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'box'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task EnumConstrainedGenericSources_AreHarmless()
    {
        // Enum values are copied immutable values: the same exemption the statics rule
        // grants where T : Enum.
        var diagnostics = await AnalyzeAsync("""
            using MemoizR;

            public class C
            {
                public void M<T>(T value) where T : System.Enum
                {
                    _ = Sending.Transfer(value);
                    _ = value;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ForeachAdvancement_IsThrowCapable()
    {
        // A custom enumerator's MoveNext/Dispose can throw after the body's handoff: the
        // catch then observes the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(IEnumerable<int> xs)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        foreach (var item in xs)
                        {
                            _ = Sending.Transfer(list);
                        }
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
    public async Task ConditionalDelegateStores_ExpandTheirArms()
    {
        // One runtime path stores the reading Touch: the guarded call can run it after the
        // handoff.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool flag)
                {
                    var list = new List<int> { 1 };
                    void Touch() => list.Add(1);
                    Action? use = flag ? Touch : null;
                    var sending = Sending.Transfer(list);
                    use?.Invoke();
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CarrierFactoryCalls_CarryTheirArguments()
    {
        // Tuple.Create stores its arguments exactly like the constructor spelling.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(Tuple.Create(list));
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ThrowingResetBodies_AreNotDefinite_ForCatchingCallers()
    {
        // Reset can fail before its reset; the caller's catch resumes with the ORIGINAL
        // transferred value still in the variable.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    void Reset()
                    {
                        MayThrow();
                        list = new List<int>();
                    }
                    try
                    {
                        _ = Sending.Transfer(list);
                        Reset();
                    }
                    catch
                    {
                    }

                    list.Add(1);
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
    public async Task TypedThrowFailures_OnlyReachMatchingCatches()
    {
        // The only failure after the handoff is an InvalidOperationException: the
        // ArgumentException handler can never observe the transferred list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool flag)
                {
                    var list = new List<int> { 1 };
                    var fresh = new List<int>();
                    try
                    {
                        _ = Sending.Transfer(list);
                        list = flag ? fresh : throw new System.InvalidOperationException();
                    }
                    catch (System.ArgumentException)
                    {
                        list.Add(1);
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CombinedDelegateInitializers_ExpandTheirOperands()
    {
        // read + noop puts BOTH callables in the invocation list: the call runs the reader
        // after the handoff.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    Action read = () => list.Add(1);
                    Action use = read + (Action)(() => { });
                    var sending = Sending.Transfer(list);
                    use();
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task OverwrittenPositionalArguments_AreNotRetained()
    {
        // The initializer definitely replaces Value: the transferred Box never keeps the
        // constructor's list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public record Box(List<int> Value);

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box(list) { Value = new List<int>() });
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SiblingArmLambdaStores_DoNotPropagateToTheOtherArm()
    {
        // The transferring lambda lands in use only on the arm that did NOT call it: no
        // path stores and then invokes before the later Add.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(bool move)
                {
                    var list = new List<int> { 1 };
                    Action use = () => { };
                    if (move)
                    {
                        use = () => { _ = Sending.Transfer(list); };
                    }
                    else
                    {
                        use();
                    }

                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task GotoCaseEdges_MakeSiblingArmsReachable()
    {
        // goto default re-enters the sibling arm AFTER the handoff: the arms are not
        // mutually exclusive on that path.
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
                        case 0:
                            _ = Sending.Transfer(list);
                            goto default;
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
    public async Task KeyValuePairCreate_IsACarrierFactory()
    {
        // KeyValuePair.Create stores both arguments exactly like its constructor.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(KeyValuePair.Create("k", list));
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task WithInitializerOverwrites_DropTheOperandSlot()
    {
        // The with-initializer definitely replaces Value: the receiver's clone never
        // retains the constructor's list.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public record Box(List<int> Value);

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new Box(list) with { Value = new List<int>() });
                    list.Add(1);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ParenthesizedTransferOperands_AreStillTransfers()
    {
        // Parentheses are pure syntax: the argument operation is the local reference.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer((list));
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CaughtEnclosingResetFailures_KeepTracking()
    {
        // The RHS can throw into the resuming catch BEFORE the assignment completes: after
        // the try the variable may still hold the transferred value.
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
                        list = MayThrow(Sending.Transfer(list));
                    }
                    catch
                    {
                    }

                    list.Add(1);
                }

                private static List<int> MayThrow(Sending<List<int>> sending) => new List<int>();
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task GotosLeavingTheSwitch_KeepSiblingArmsExclusive()
    {
        // goto after exits the switch entirely: the default arm never runs on the
        // transferred path.
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
                        case 0:
                            _ = Sending.Transfer(list);
                            goto after;
                        default:
                            list.Add(1);
                            break;
                    }

                    after:
                    _ = n;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AnonymousObjectInitializers_AreTransferSources()
    {
        // Transfer(new { Value = list }) hands off an object graph containing the same list:
        // anonymous members deterministically store what they were built from.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new { Value = list });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ArrayInitializerElements_AreTransferSources()
    {
        // Transfer(new[] { list }) hands off the array WITH its element: the array carries
        // the reference exactly like a tuple does.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    var sending = Sending.Transfer(new[] { list });
                    list.Add(1);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task FieldStoredDelegates_InvokedThroughTheField_AreUses()
    {
        // The reading lambda lives in a FIELD slot: invoking through the field after the
        // handoff runs it exactly like a local-held delegate would.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private Action? use;

                public void M()
                {
                    var list = new List<int> { 1 };
                    this.use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    this.use!();
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
        Assert.Contains("'list'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task FieldStoredDelegates_OverwrittenBeforeTheInvocation_AreQuiet()
    {
        // The definite overwrite replaces the field's invocation list before the call:
        // the reading lambda is no longer what `this.use!()` runs.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                private Action? use;

                public void M()
                {
                    var list = new List<int> { 1 };
                    this.use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    this.use = () => { };
                    this.use!();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FilteredCatchesOfUnrelatedTypes_CannotSeeAnExplicitThrow()
    {
        // C# tests the clause TYPE before running any filter: a filtered catch of an
        // unrelated type can never receive the InvalidOperationException, so the handler
        // never observes the transferred value.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sending = Sending.Transfer(list);
                        throw new InvalidOperationException();
                    }
                    catch (ArgumentException) when (MayPass())
                    {
                        list.Add(1);
                    }
                }

                private static bool MayPass() => true;
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FilteredCatchesOfMatchingTypes_StillSeeAnExplicitThrow()
    {
        // The type gate passes, so whether the handler runs is down to the filter -- which
        // may pass: the catch stays reachable and its read reports.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sending = Sending.Transfer(list);
                        throw new InvalidOperationException();
                    }
                    catch (InvalidOperationException) when (MayPass())
                    {
                        list.Add(1);
                    }
                }

                private static bool MayPass() => true;
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task EnclosingResetFailures_AtOrBeforeTheHandoff_DoNotOpenTheWindow()
    {
        // The only throw-capable operation in the reinitializing RHS is the Transfer call
        // itself: if it throws, no wrapper escaped; if it succeeds, the local is reset
        // before the catch could ever run. The handler never sees a transferred value.
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
                        list = (Sending.Transfer(list), (List<int>)null!).Item2;
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
    public async Task NestedDelegateStores_ExecutedOnlyAfterTheCall_AreNotInTheInvocationList()
    {
        // Configure() runs only AFTER use(): the store it carries cannot be in the
        // delegate's invocation list at the call, which still runs the no-op.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => { };
                    void Touch() { list.Add(1); }
                    void Configure() { use = Touch; }
                    var sending = Sending.Transfer(list);
                    use();
                    Configure();
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NestedDelegateStores_WhoseCallRanBeforeTheConsumer_StayInTheInvocationList()
    {
        // Configure() ran before the handoff: at use() the reading target IS the delegate's
        // current value.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M()
                {
                    var list = new List<int> { 1 };
                    Action use = () => { };
                    void Touch() { list.Add(1); }
                    void Configure() { use = Touch; }
                    Configure();
                    var sending = Sending.Transfer(list);
                    use();
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task ConditionalDelegateReturns_AreEscapes()
    {
        // The stored reading delegate leaves through a conditional expression: callers can
        // receive and invoke it after the handoff.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Action? M(bool flag)
                {
                    var list = new List<int> { 1 };
                    Action use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    return flag ? use : null;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task CoalesceDelegateReturns_AreEscapes()
    {
        // `use ?? fallback` publishes the stored reading delegate exactly like a bare
        // return of it would.
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public Action M(Action fallback)
                {
                    var list = new List<int> { 1 };
                    Action? use = () => list.Add(1);
                    var sending = Sending.Transfer(list);
                    return use ?? fallback;
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MZR005", diagnostic.Id);
    }

    [Fact]
    public async Task LockEntries_AreThrowCapable_AndKeepTheirCatchesReachable()
    {
        // The hidden Monitor.Enter can throw (a null gate) after the handoff: the catch
        // runs while the transferred list is still sender-reachable.
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using MemoizR;

            public class C
            {
                public void M(object? maybeGate)
                {
                    var list = new List<int> { 1 };
                    try
                    {
                        var sending = Sending.Transfer(list);
                        lock (maybeGate!) { }
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
}
