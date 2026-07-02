using System.Windows;
using System.Windows.Threading;

namespace MemoizR;

// WPF wiring for the thread-pool/UI-thread split (#13): signals, memos and every reaction's
// dependency evaluation stay on the thread pool; only the reaction's action is marshalled to
// the WPF UI thread, through a DispatcherSynchronizationContext (Dispatcher.BeginInvoke) --
// the async equivalent of Application.Current.Dispatcher.Invoke(() => { ... }).
public static class WpfMemoFactory
{
    /// <summary>
    /// Routes the actions of reactions built from this factory to the UI thread of
    /// <see cref="Application.Current"/>; dependency evaluation stays on the thread pool.
    /// Callable from any thread, but only once the WPF <see cref="Application"/> exists.
    /// </summary>
    public static MemoFactory AddWpfDispatcher(this MemoFactory memoFactory)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException(
                "Application.Current is null: create the WPF Application before calling AddWpfDispatcher, or pass a Dispatcher explicitly.");
        return memoFactory.AddWpfDispatcher(application.Dispatcher);
    }

    /// <summary>
    /// Routes the actions of reactions built from this factory to the thread of the given
    /// <paramref name="dispatcher"/>; dependency evaluation stays on the thread pool.
    /// </summary>
    public static MemoFactory AddWpfDispatcher(this MemoFactory memoFactory, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return memoFactory.AddExecutor(new WpfDispatcherExecutor(dispatcher));
    }
}

// A dispatcher-backed IExecutor. Unlike wrapping a DispatcherSynchronizationContext in a
// SynchronizationContextExecutor (whose IsCurrent compares the wrapped instance by reference and
// so reads false against the DIFFERENT DispatcherSynchronizationContext WPF installs on its own
// loop), IsCurrent here asks the dispatcher directly via CheckAccess -- true exactly when the
// caller is on the dispatcher thread -- so executor.AssertIsolated() works for WPF-dispatched
// reaction actions.
internal sealed class WpfDispatcherExecutor(Dispatcher dispatcher) : IExecutor
{
    public void Enqueue(Action work) => dispatcher.BeginInvoke(work);

    public bool IsCurrent => dispatcher.CheckAccess();
}
