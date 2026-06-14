using System.Windows;
using System.Windows.Threading;

namespace MemoizR.Wpf.Tests;

// Hosts THE WPF Application of the test process on a background STA thread. A process may only
// ever construct one System.Windows.Application (a second ctor throws, even after Shutdown), so
// every test that needs Application.Current shares this collection fixture. That also means the
// AddWpfDispatcher() no-Application guard is only observable BEFORE the single Application
// exists -- xunit cannot express "this test must run first" -- so the fixture probes the guard
// in its constructor and records the outcome for the guard test to assert on.
public sealed class WpfApplicationFixture : IDisposable
{
    public Exception? GuardProbeResult { get; }
    public Application Application { get; }
    public int UiThreadId => Application.Dispatcher.Thread.ManagedThreadId;

    private readonly Thread uiThread;

    public WpfApplicationFixture()
    {
        try
        {
            new MemoFactory().AddWpfDispatcher();
            GuardProbeResult = null;
        }
        catch (Exception e)
        {
            GuardProbeResult = e;
        }

        var ready = new TaskCompletionSource<Application>(TaskCreationOptions.RunContinuationsAsynchronously);
        uiThread = new Thread(() =>
        {
            try
            {
                // OnExplicitShutdown: no window ever opens, so the default last-window-closed
                // shutdown could never fire anyway; Run() pumps the dispatcher until Dispose.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.SetResult(app);
                app.Run();
            }
            catch (Exception e)
            {
                ready.TrySetException(e);
            }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.IsBackground = true;
        uiThread.Name = "MemoizR.Wpf.Tests UI thread";
        uiThread.Start();
        Application = ready.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Application.Dispatcher.Invoke(Application.Shutdown);
        uiThread.Join(TimeSpan.FromSeconds(10));
    }
}

[CollectionDefinition("WpfApplication")]
public class WpfApplicationCollection : ICollectionFixture<WpfApplicationFixture>
{
}
