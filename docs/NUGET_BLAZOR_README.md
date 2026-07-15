# MemoizR.Blazor: Thread-pool reactivity, renderer-context reactions

Blazor wiring for MemoizR.Reactive: signals, memos and every reaction's dependency evaluation
run on the thread pool, and only the reaction's action is marshalled to the renderer's
dispatcher — the circuit's synchronization context on Blazor Server, the UI thread on
WebAssembly. Blazor Server circuits are genuinely multi-threaded, which is exactly what
MemoizR's cross-flow correctness guarantees are for.

Register one reactive graph per circuit:

```csharp
builder.Services.AddMemoizR();
```

Bind graph nodes into components — renders stay synchronous while the graph stays async:

```csharp
public partial class TodoList : MemoizRComponentBase
{
    private ImmutableList<string> todos = ImmutableList<string>.Empty;
    private OptimisticState<ImmutableList<string>> optimistic = default!;
    private ReactiveAction<string> addTodo = default!;

    protected override void OnInitialized()
    {
        var source = MemoFactory.CreateSignal(ImmutableList<string>.Empty);
        optimistic = MemoFactory.CreateOptimistic<ImmutableList<string>>(source);
        addTodo = MemoFactory.CreateAction<string>(async (todo, ctx) =>
        {
            await ctx.Apply(optimistic, list => list.Add($"{todo} (pending)")); // instant
            var confirmed = await Api.SaveAsync(todo, ctx.Token);               // the process
            await source.Set((await source.Get()).Add(confirmed));              // confirm
        }); // fault/cancel => automatic rollback

        Bind(optimistic, value => todos = value); // dependency eval on the thread pool,
                                                  // field write + StateHasChanged via InvokeAsync
    }
}
```

```razor
<button disabled="@addTodo.IsPendingSnapshot" @onclick='() => addTodo.Run(newTodo)'>Add</button>
@foreach (var todo in todos) { <TodoRow Item="@todo" /> }
```

Transitions (`MemoFactory.BeginTransition()`), pending indicators and optimistic state come
from MemoizR.Reactive; see the [MemoizR repository](https://github.com/timonkrebs/MemoizR)
and ADR 0007 for the design. For reactions owned by services rather than components, route a
factory's actions to a renderer dispatcher directly:

```csharp
var f = new MemoFactory().AddBlazorDispatcher(dispatcher);
```
