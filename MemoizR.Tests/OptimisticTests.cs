using System.Collections.Immutable;
using MemoizR.Reactive;

namespace MemoizR.Tests;

// Phase 3 of ADR 0007: optimistic state with structural rollback, driven by process-layer
// actions. The test matrix is Solid 2.0's transition lifecycle table: initial -> optimistic
// projection -> awaiting resolution -> confirm-and-refresh | rollback.
public class OptimisticTests
{
    [Fact(Timeout = 5000)]
    public async Task Optimistic_ProjectsInstantly_ThenConfirmsAndDropsThePatch()
    {
        var f = new MemoFactory();
        var todos = f.CreateSignal(ImmutableList.Create("existing"));
        var optimistic = f.CreateOptimistic<ImmutableList<string>>(todos);
        Assert.Equal(new[] { "existing" }, await optimistic.Get());

        var server = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var addTodo = f.CreateAction<string>(async (todo, ctx) =>
        {
            await ctx.Apply(optimistic, list => list.Add($"{todo} (pending)"));
            var confirmed = await server.Task;                       // the network call
            await todos.Set((await todos.Get()).Add(confirmed));     // confirm the source of truth
        });

        var run = addTodo.Run("write docs");

        // 2. Action triggered: the view instantly projects the future state; the source of
        // truth is untouched while the "server" is still deciding.
        await TestHelpers.WaitForConvergenceAsync(() => optimistic.Get().Result.Count == 2);
        Assert.Equal(new[] { "existing", "write docs (pending)" }, await optimistic.Get());
        Assert.Equal(new[] { "existing" }, await todos.Get());

        // 4. Server commit: the confirmed value replaces the projection, the patch is dropped.
        server.SetResult("write docs");
        await run.Completion;
        await run.Settled;
        Assert.Equal(new[] { "existing", "write docs" }, await optimistic.Get());
        Assert.Equal(new[] { "existing", "write docs" }, await todos.Get());
    }

    [Fact(Timeout = 5000)]
    public async Task Optimistic_RollsBackOnFault_WithoutTouchingTheSource()
    {
        var f = new MemoFactory();
        var value = f.CreateSignal(10);
        var optimistic = f.CreateOptimistic<int>(value);
        Assert.Equal(10, await optimistic.Get());

        var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bump = f.CreateAction<int>(async (delta, ctx) =>
        {
            await ctx.Apply(optimistic, x => x + delta);
            await server.Task;
            throw new InvalidOperationException("server rejected");
        });

        var run = bump.Run(5);
        await TestHelpers.WaitForConvergenceAsync(() => optimistic.Get().Result == 15);

        // 5. Rollback: the temporary projection vanishes, no manual recovery logic anywhere.
        server.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Completion);
        await run.Settled; // the rollback's own effect wavefront settles cleanly
        Assert.Equal(10, await optimistic.Get());
        Assert.Equal(10, await value.Get());
    }

    [Fact(Timeout = 5000)]
    public async Task Optimistic_RollsBackOnCancellation()
    {
        var f = new MemoFactory();
        var value = f.CreateSignal(10);
        var optimistic = f.CreateOptimistic<int>(value);

        var bump = f.CreateAction<int>(async (delta, ctx) =>
        {
            await ctx.Apply(optimistic, x => x + delta);
            await Task.Delay(Timeout.Infinite, ctx.Token);
        });

        var run = bump.Run(5);
        await TestHelpers.WaitForConvergenceAsync(() => optimistic.Get().Result == 15);

        run.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion);
        await run.Settled;
        Assert.Equal(10, await optimistic.Get());
    }

    [Fact(Timeout = 5000)]
    public async Task Optimistic_OverlappingActions_RollBackIndependently()
    {
        var f = new MemoFactory();
        var items = f.CreateSignal(ImmutableList<string>.Empty);
        var optimistic = f.CreateOptimistic<ImmutableList<string>>(items);

        var serverA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var add = f.CreateAction<(string Item, Task Server, bool Fail)>(async (p, ctx) =>
        {
            await ctx.Apply(optimistic, list => list.Add(p.Item));
            await p.Server;
            if (p.Fail)
            {
                throw new InvalidOperationException("rejected");
            }
            await items.Set((await items.Get()).Add(p.Item));
        });

        var runA = add.Run(("a", serverA.Task, true));
        var runB = add.Run(("b", serverB.Task, false));
        await TestHelpers.WaitForConvergenceAsync(() => optimistic.Get().Result.Count == 2);

        // A fails: only A's projection vanishes -- B's stays, because rollback is patch
        // removal, not a compensating write over shared state.
        serverA.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runA.Completion);
        await runA.Settled;
        Assert.Equal(new[] { "b" }, await optimistic.Get());
        Assert.Empty(await items.Get() ?? []);

        serverB.SetResult();
        await runB.Completion;
        await runB.Settled;
        Assert.Equal(new[] { "b" }, await optimistic.Get());
        Assert.Equal(new[] { "b" }, await items.Get());
    }

    [Fact(Timeout = 5000)]
    public async Task Action_IsPending_CoversTheWholeRun()
    {
        var f = new MemoFactory();
        var value = f.CreateSignal(1);
        var optimistic = f.CreateOptimistic<int>(value);
        var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var act = f.CreateAction<int>(async (x, ctx) =>
        {
            await ctx.Apply(optimistic, v => v + x);
            await server.Task;
        });

        Assert.False(act.IsPendingSnapshot);
        Assert.False(await act.IsPending.Get());

        var run = act.Run(1);
        await TestHelpers.WaitForConvergenceAsync(() => act.IsPending.Get().Result);
        Assert.True(act.IsPendingSnapshot);

        server.SetResult();
        await run.Completion;
        Assert.False(act.IsPendingSnapshot);
        await TestHelpers.WaitForConvergenceAsync(() => !act.IsPending.Get().Result);
    }

    [Fact(Timeout = 5000)]
    public async Task ActionRun_Settled_MeansTheUiReflectsTheFinalOutcome()
    {
        var f = new MemoFactory();
        var todos = f.CreateSignal(ImmutableList.Create("existing"));
        var optimistic = f.CreateOptimistic<ImmutableList<string>>(todos);

        ImmutableList<string> observed = ImmutableList<string>.Empty;
        var r = f.BuildReaction().CreateReaction(optimistic, list => Volatile.Write(ref observed, list));
        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref observed).Count == 1);

        var addTodo = f.CreateAction<string>(async (todo, ctx) =>
        {
            await ctx.Apply(optimistic, list => list.Add($"{todo} (pending)"));
            await todos.Set((await todos.Get()).Add(todo));
        });

        var run = addTodo.Run("ship it");
        await run.Completion;
        await run.Settled;

        // The patch-drop is the run's LAST write, so settlement guarantees the reaction's
        // committed view reflects it: the confirmed item, no pending duplicate.
        Assert.Equal(new[] { "existing", "ship it" }, Volatile.Read(ref observed));
    }
}
