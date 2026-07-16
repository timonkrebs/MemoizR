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
}
