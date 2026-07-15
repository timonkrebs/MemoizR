using Microsoft.AspNetCore.Components;

namespace MemoizR;

// Blazor wiring for the thread-pool/UI split (#13, ADR 0007 phase 4), mirroring
// MemoizR.Wpf: signals, memos and every reaction's dependency evaluation stay on the thread
// pool; only the reaction's action is marshalled to the renderer's dispatcher -- on Blazor
// Server that is the circuit's synchronization context, on WebAssembly the single UI thread.
public static class BlazorMemoFactory
{
    /// <summary>
    /// Routes the actions of reactions built from this factory to the given renderer
    /// <see cref="Dispatcher"/>; dependency evaluation stays on the thread pool. Components
    /// that own their bindings should prefer <see cref="MemoizRComponentBase"/>, which
    /// marshals through the component's own <c>InvokeAsync</c> instead.
    /// </summary>
    public static MemoFactory AddBlazorDispatcher(this MemoFactory memoFactory, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return memoFactory.AddExecutor(new BlazorDispatcherExecutor(dispatcher));
    }
}

// A dispatcher-backed IExecutor, the exact Blazor mirror of WpfDispatcherExecutor: IsCurrent
// asks the dispatcher directly via CheckAccess -- true exactly when the caller is on the
// renderer's synchronization context -- so executor.AssertIsolated() works for dispatched
// reaction actions.
internal sealed class BlazorDispatcherExecutor(Dispatcher dispatcher) : IExecutor
{
    public void Enqueue(Action work) => _ = dispatcher.InvokeAsync(work);

    public bool IsCurrent => dispatcher.CheckAccess();
}
