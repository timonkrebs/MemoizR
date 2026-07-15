using MemoizR.Reactive;

namespace MemoizR;

/// <summary>
/// Binds reactive nodes to a UI surface (ADR 0007 phase 4): each <see cref="Bind"/> builds a
/// reaction whose dependency evaluation runs on the thread pool and whose apply-and-notify
/// step is marshalled through the supplied dispatch delegate (a component's
/// <c>InvokeAsync</c>). Renders are synchronous, MemoizR reads are async -- so the binder's
/// applied values ARE the render snapshot: markup reads the fields the apply callbacks wrote,
/// never the graph directly. Dispose with the component; disposing stops all bindings.
/// Component authors normally use <see cref="MemoizRComponentBase"/> instead of this directly.
/// </summary>
public sealed class ReactionBinder : IDisposable
{
    private readonly MemoFactory factory;
    private readonly IExecutor executor;
    private readonly Action notifyChanged;
    private readonly Lock gate = new();
    private readonly List<IDisposable> reactions = new();
    private bool disposed;

    /// <param name="factory">The factory whose graph the bindings observe.</param>
    /// <param name="dispatch">Marshals work onto the UI surface -- pass the component's
    /// <c>InvokeAsync</c>. Faults thrown by the dispatched work surface through the reaction's
    /// fault machinery (the binding stays dirty and re-runs on the next change).</param>
    /// <param name="notifyChanged">Called after a binding applied a new value, on the UI
    /// surface -- pass <c>StateHasChanged</c>.</param>
    public ReactionBinder(MemoFactory factory, Func<Action, Task> dispatch, Action notifyChanged)
    {
        this.factory = factory;
        this.notifyChanged = notifyChanged;
        executor = new DispatchExecutor(dispatch);
    }

    /// <summary>
    /// Observes <paramref name="source"/>: on every committed change, <paramref name="apply"/>
    /// runs on the UI surface with the new value (write it into a component field), followed
    /// by the change notification. The initial value is applied eagerly.
    /// </summary>
    public void Bind<T>(IStateGetR<T> source, Action<T> apply)
    {
        var reaction = factory.BuildReaction("Blazor.Bind")
            .AddExecutor(executor)
            .CreateReaction(source, value =>
            {
                apply(value);
                notifyChanged();
            });

        lock (gate)
        {
            if (disposed)
            {
                reaction.Dispose();
                return;
            }
            reactions.Add(reaction);
        }
    }

    public void Dispose()
    {
        IDisposable[] toDispose;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            toDispose = [.. reactions];
            reactions.Clear();
        }

        foreach (var reaction in toDispose)
        {
            reaction.Dispose();
        }
    }

    // InvokeAsync-backed executor. IsCurrent is conservatively false (a component exposes no
    // CheckAccess), which only costs an extra hop: the renderer's dispatcher already inlines
    // an InvokeAsync issued from its own context.
    private sealed class DispatchExecutor(Func<Action, Task> dispatch) : IExecutor
    {
        public void Enqueue(Action work) => _ = dispatch(work);

        public bool IsCurrent => false;
    }
}
