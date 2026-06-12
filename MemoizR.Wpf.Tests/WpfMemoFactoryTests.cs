using System.Windows.Threading;
using MemoizR.Reactive;

namespace MemoizR.Wpf.Tests;

// Tests that need Application.Current share the process-wide Application hosted by the fixture.
[Collection("WpfApplication")]
public class WpfDispatcherTests
{
    private readonly WpfApplicationFixture fixture;

    public WpfDispatcherTests(WpfApplicationFixture fixture)
    {
        this.fixture = fixture;
    }

    // The guard is only observable before the process-wide Application exists, so the fixture
    // probed it in its constructor (see WpfApplicationFixture); this asserts the recorded outcome.
    [Fact]
    public void AddWpfDispatcher_WithoutApplication_ThrowsInvalidOperation()
    {
        var ex = Assert.IsType<InvalidOperationException>(fixture.GuardProbeResult);
        Assert.Contains("Application", ex.Message);
    }

    [Fact]
    public void AddWpfDispatcher_NullDispatcher_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoFactory().AddWpfDispatcher(null!));
    }

    // The #13 contract on real WPF: dependency evaluation (the memo computations) must stay off
    // the UI thread, and only the action -- with the already-evaluated values -- may run on it.
    [Fact(Timeout = 30000)]
    public async Task AddWpfDispatcher_EvaluatesDependenciesOnWorkers_RunsActionOnUiThread()
    {
        var f = new MemoFactory().AddWpfDispatcher(); // resolves Application.Current.Dispatcher
        var v1 = f.CreateSignal(1);
        var v2 = f.CreateSignal(10);
        var memoRanOnUiThread = false;
        var m1 = f.CreateMemoizR(async () =>
        {
            memoRanOnUiThread |= Environment.CurrentManagedThreadId == fixture.UiThreadId;
            return await v1.Get() * 2;
        });

        var last = 0;
        var actionRanOffUiThread = false;
        var r = f.BuildReaction().CreateReaction(m1, v2, (a, b) =>
        {
            actionRanOffUiThread |= Environment.CurrentManagedThreadId != fixture.UiThreadId;
            last = a + b;
        });

        await WpfTestHelpers.WaitForConvergenceAsync(() => last == 12);
        Assert.Equal(12, last);

        await v1.Set(5); // a worker-side write; the whole propagation path stays off the UI thread
        await WpfTestHelpers.WaitForConvergenceAsync(() => last == 20);
        Assert.Equal(20, last);

        Assert.False(memoRanOnUiThread, "dependency evaluation ran on the WPF UI thread; it must stay on the thread pool");
        Assert.False(actionRanOffUiThread, "the action ran off the WPF UI thread");
        GC.KeepAlive(r);
    }

    // Realistic WPF usage: the factory and the whole graph are created ON the UI thread (e.g. in
    // a view model constructor). Even the reaction's eager initial run must evaluate its
    // dependencies on the thread pool -- only the action comes back to the UI thread. Guards the
    // ForceYielding in ReactionBase.RunDebouncedUpdateAsync end to end: a plain
    // ConfigureAwait(false) continued inline on the UI thread (Task.Delay(zero) completes
    // synchronously), and a Task.Yield would re-queue the update -- and the whole graph
    // evaluation -- right back onto the dispatcher, because it captures the scheduling thread's
    // SynchronizationContext. Only a context-free yield keeps the evaluation on workers.
    [Fact(Timeout = 30000)]
    public async Task AddWpfDispatcher_GraphCreatedOnUiThread_StillEvaluatesDependenciesOnWorkers()
    {
        var memoRanOnUiThread = false;
        var actionRanOffUiThread = false;
        var last = 0;
        Signal<int>? v1 = null;
        Reaction? r = null;

        await fixture.Application.Dispatcher.InvokeAsync(() =>
        {
            var f = new MemoFactory().AddWpfDispatcher();
            v1 = f.CreateSignal(1);
            var m1 = f.CreateMemoizR(async () =>
            {
                memoRanOnUiThread |= Environment.CurrentManagedThreadId == fixture.UiThreadId;
                return await v1!.Get() * 2;
            });
            r = f.BuildReaction().CreateReaction(m1, v =>
            {
                actionRanOffUiThread |= Environment.CurrentManagedThreadId != fixture.UiThreadId;
                last = v;
            });
        });

        await WpfTestHelpers.WaitForConvergenceAsync(() => last == 2);
        Assert.Equal(2, last);

        await v1!.Set(5);
        await WpfTestHelpers.WaitForConvergenceAsync(() => last == 10);
        Assert.Equal(10, last);

        Assert.False(memoRanOnUiThread, "dependency evaluation ran on the WPF UI thread; it must stay on the thread pool even for the initial run");
        Assert.False(actionRanOffUiThread, "the action ran off the WPF UI thread");
        GC.KeepAlive(r);
    }

    [Fact(Timeout = 30000)]
    public async Task AddWpfDispatcher_ResumeFromUiThread_StillEvaluatesDependenciesOnWorkers()
    {
        var f = new MemoFactory().AddWpfDispatcher();
        var v1 = f.CreateSignal(1);
        var memoRanOnUiThread = false;
        var m1 = f.CreateMemoizR(async () =>
        {
            memoRanOnUiThread |= Environment.CurrentManagedThreadId == fixture.UiThreadId;
            return await v1.Get() * 2;
        });

        var last = 0;
        var actionRanOffUiThread = false;
        var r = f.BuildReaction().CreateReaction(m1, v =>
        {
            actionRanOffUiThread |= Environment.CurrentManagedThreadId != fixture.UiThreadId;
            last = v;
        });

        await WpfTestHelpers.WaitForConvergenceAsync(() => last == 2);
        Assert.Equal(2, last);

        memoRanOnUiThread = false;
        actionRanOffUiThread = false;
        r.Pause();
        await v1.Set(5);
        await Task.Delay(100);
        Assert.Equal(2, last);

        await await fixture.Application.Dispatcher.InvokeAsync(async () => await r.Resume());

        Assert.Equal(10, last);
        Assert.False(memoRanOnUiThread, "dependency evaluation ran on the WPF UI thread during Resume; it must stay on the thread pool");
        Assert.False(actionRanOffUiThread, "the action ran off the WPF UI thread");
        GC.KeepAlive(r);
    }

    // A throwing action must fault the awaited update exactly once (observable via Resume) and
    // must NOT escape onto the dispatcher as DispatcherUnhandledException -- in a real app that
    // is a process crash -- and the reaction must recover on the next Set.
    [Fact(Timeout = 30000)]
    public async Task AddWpfDispatcher_ActionThrows_FaultsResumeWithoutDispatcherUnhandledException()
    {
        var dispatcher = fixture.Application.Dispatcher;
        var escaped = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            escaped.Enqueue(e.Exception);
            e.Handled = true; // keep a regression from tearing down the shared test Application
        }

        dispatcher.Invoke(() => fixture.Application.DispatcherUnhandledException += OnUnhandled);
        try
        {
            var f = new MemoFactory().AddWpfDispatcher(dispatcher);
            var v1 = f.CreateSignal(1);
            var last = 0;
            var r = f.BuildReaction().CreateReaction(v1, v =>
            {
                if (v == 13) throw new InvalidOperationException("boom13");
                last = v;
            });

            await WpfTestHelpers.WaitForConvergenceAsync(() => last == 1);
            Assert.Equal(1, last);

            r.Pause();
            await v1.Set(13);
            // Resume runs the pending update inline, so the action's fault propagates to the caller.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Resume());
            Assert.Contains("boom13", ex.Message);

            // The failed run must not have poisoned the reaction: the next write still triggers it.
            await v1.Set(2);
            await WpfTestHelpers.WaitForConvergenceAsync(() => last == 2);
            Assert.Equal(2, last);

            Assert.True(escaped.IsEmpty,
                $"exception escaped onto the dispatcher: {string.Join(", ", escaped)}");
            GC.KeepAlive(r);
        }
        finally
        {
            dispatcher.Invoke(() => fixture.Application.DispatcherUnhandledException -= OnUnhandled);
        }
    }
}

// Tests that must not touch the shared Application: the explicit-Dispatcher overload against a
// standalone dispatcher thread (multi-dispatcher setups need no Application at all).
public class WpfExplicitDispatcherTests
{
    [Fact(Timeout = 30000)]
    public async Task AddWpfDispatcher_ExplicitDispatcher_RunsActionOnThatDispatchersThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher); // creates this thread's dispatcher
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "MemoizR.Wpf.Tests standalone dispatcher";
        thread.Start();
        var dispatcher = await ready.Task;

        try
        {
            var f = new MemoFactory().AddWpfDispatcher(dispatcher);
            var v1 = f.CreateSignal(1);
            var last = 0;
            var actionRanOffDispatcherThread = false;
            var r = f.BuildReaction().CreateReaction(v1, v =>
            {
                actionRanOffDispatcherThread |= Environment.CurrentManagedThreadId != dispatcher.Thread.ManagedThreadId;
                last = v;
            });

            await WpfTestHelpers.WaitForConvergenceAsync(() => last == 1);
            Assert.Equal(1, last);

            await v1.Set(7);
            await WpfTestHelpers.WaitForConvergenceAsync(() => last == 7);
            Assert.Equal(7, last);

            Assert.False(actionRanOffDispatcherThread, "the action ran off the supplied dispatcher's thread");
            GC.KeepAlive(r);
        }
        finally
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            thread.Join(TimeSpan.FromSeconds(10));
        }
    }
}
