using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MemoizR.Reactive;

namespace MemoizR.Blazor.Tests;

// Phase 4 of ADR 0007, tested against Blazor's real renderer dispatcher
// (Dispatcher.CreateDefault -- the same implementation Blazor Server circuits use), so the
// thread-pool/renderer-context split is exercised without spinning up a renderer.
public class BlazorIntegrationTests
{
    private static async Task WaitForConvergenceAsync(Func<bool> converged, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!converged() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task BlazorDispatcherExecutor_RunsActionsOnTheDispatcher_DependenciesOffIt()
    {
        var dispatcher = Dispatcher.CreateDefault();
        var f = new MemoFactory().AddBlazorDispatcher(dispatcher);

        var v = f.CreateSignal(1);
        var computedOnDispatcher = true;
        var m = f.CreateMemoizR(async () =>
        {
            // Dependency evaluation must stay off the renderer's context.
            computedOnDispatcher &= dispatcher.CheckAccess();
            return await v.Get() * 2;
        });

        var observed = 0;
        var actionOnDispatcher = false;
        var r = f.BuildReaction().CreateReaction(m, value =>
        {
            actionOnDispatcher = dispatcher.CheckAccess();
            Volatile.Write(ref observed, value);
        });

        await v.Set(5);
        await WaitForConvergenceAsync(() => Volatile.Read(ref observed) == 10);

        Assert.True(actionOnDispatcher);
        Assert.False(computedOnDispatcher);
    }

    [Fact(Timeout = 5000)]
    public async Task ReactionBinder_AppliesValuesAndNotifies_ThroughTheDispatchDelegate()
    {
        var dispatcher = Dispatcher.CreateDefault();
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await v.Get() * 2);

        // The component-side snapshot the render would read, plus the StateHasChanged count.
        var snapshot = 0;
        var renders = 0;
        var appliedOnDispatcher = true;
        var binder = new ReactionBinder(
            f,
            work => dispatcher.InvokeAsync(work),
            () => Interlocked.Increment(ref renders));

        binder.Bind(m, value =>
        {
            appliedOnDispatcher &= dispatcher.CheckAccess();
            Volatile.Write(ref snapshot, value);
        });

        await WaitForConvergenceAsync(() => Volatile.Read(ref snapshot) == 2);
        await v.Set(5);
        await WaitForConvergenceAsync(() => Volatile.Read(ref snapshot) == 10);

        Assert.True(appliedOnDispatcher);
        Assert.True(Volatile.Read(ref renders) >= 2); // initial apply + the change

        // Disposing the binder stops the bindings: no further applies or renders.
        binder.Dispose();
        var rendersAtDispose = Volatile.Read(ref renders);
        await v.Set(7);
        await Task.Delay(100);
        Assert.Equal(10, Volatile.Read(ref snapshot));
        Assert.Equal(rendersAtDispose, Volatile.Read(ref renders));
    }

    [Fact(Timeout = 5000)]
    public async Task ReactionBinder_DrivesPendingIndicators()
    {
        var dispatcher = Dispatcher.CreateDefault();
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var optimistic = f.CreateOptimistic<int>(v);
        var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var act = f.CreateAction<int>(async (delta, ctx) =>
        {
            await ctx.Apply(optimistic, x => x + delta);
            await server.Task;
        });

        // The disabled-button binding: a component field driven by the action's pending flag.
        var buttonDisabled = false;
        var value = 0;
        var binder = new ReactionBinder(f, work => dispatcher.InvokeAsync(work), () => { });
        binder.Bind(act.IsPending, pending => Volatile.Write(ref buttonDisabled, pending));
        binder.Bind(optimistic, x => Volatile.Write(ref value, x));

        await WaitForConvergenceAsync(() => Volatile.Read(ref value) == 1);
        var run = act.Run(5);
        await WaitForConvergenceAsync(() => Volatile.Read(ref buttonDisabled) && Volatile.Read(ref value) == 6);

        server.SetResult();
        await run.Completion;
        await run.Settled;
        await WaitForConvergenceAsync(() => !Volatile.Read(ref buttonDisabled) && Volatile.Read(ref value) == 1);
        Assert.False(Volatile.Read(ref buttonDisabled));
        Assert.Equal(1, Volatile.Read(ref value)); // no confirm write in this action: rolled forward to source truth

        binder.Dispose();
    }

    [Fact]
    public void AddMemoizR_RegistersOneFactoryPerScope()
    {
        var services = new ServiceCollection().AddMemoizR().BuildServiceProvider();

        using var circuit1 = services.CreateScope();
        using var circuit2 = services.CreateScope();

        var f1a = circuit1.ServiceProvider.GetRequiredService<MemoFactory>();
        var f1b = circuit1.ServiceProvider.GetRequiredService<MemoFactory>();
        var f2 = circuit2.ServiceProvider.GetRequiredService<MemoFactory>();

        Assert.Same(f1a, f1b);   // one graph per circuit
        Assert.NotSame(f1a, f2); // circuits are isolated
    }
}
