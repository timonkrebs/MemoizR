using Microsoft.AspNetCore.Components;

namespace MemoizR;

/// <summary>
/// Component base for MemoizR-driven Blazor UIs (ADR 0007 phase 4). Call
/// <see cref="Bind{T}"/> from <c>OnInitialized</c> to project graph nodes into component
/// fields: dependency evaluation runs on the thread pool, and only the field write plus
/// <c>StateHasChanged</c> are marshalled through the component's <c>InvokeAsync</c>. Markup
/// then reads those fields (and sync snapshots like <c>Transition.IsPending</c> /
/// <c>ReactionBase.IsPendingSnapshot</c>) -- renders stay synchronous while the graph stays
/// async. Bindings are disposed with the component.
/// </summary>
public abstract class MemoizRComponentBase : ComponentBase, IDisposable
{
    private ReactionBinder? binder;

    /// <summary>The scoped factory registered by <c>services.AddMemoizR()</c> -- per circuit
    /// on Blazor Server, per app on WebAssembly.</summary>
    [Inject]
    protected MemoFactory MemoFactory { get; set; } = default!;

    /// <summary>The component's binder; created on first use. Only call after initialization
    /// has started (<c>OnInitialized</c> or later), when <c>InvokeAsync</c> is available.</summary>
    protected ReactionBinder Binder => binder ??= new(MemoFactory, work => InvokeAsync(work), StateHasChanged);

    /// <summary>Projects <paramref name="source"/> into the component: <paramref name="apply"/>
    /// writes the value into a field on the renderer's context, then the component re-renders.</summary>
    protected void Bind<T>(IStateGetR<T> source, Action<T> apply) => Binder.Bind(source, apply);

    public virtual void Dispose()
    {
        binder?.Dispose();
        GC.SuppressFinalize(this);
    }
}
