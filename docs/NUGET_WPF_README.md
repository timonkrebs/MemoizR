# MemoizR.Wpf: Thread-pool reactivity, UI-thread reactions

WPF wiring for MemoizR.Reactive: signals, memos and every reaction's dependency evaluation run
on the thread pool, and only the reaction's action is marshalled to the WPF UI thread — so the
UI thread only ever handles the state that is actually relevant for the UI.

```csharp
var f = new MemoFactory().AddWpfDispatcher(); // uses Application.Current.Dispatcher

var v1 = f.CreateSignal(1);
var m1 = f.CreateMemoizR(async () => await v1.Get() * 2); // computed on worker threads

// Dependencies are separate parameters so they are evaluated in parallel on the thread
// pool; only the action below runs on the UI thread, with the already-computed values.
var r1 = f.BuildReaction().CreateReaction(m1, v => viewModel.Value = v);

await v1.Set(5); // safe from any thread
```

For multi-dispatcher setups a specific dispatcher can be supplied:

```csharp
var f = new MemoFactory().AddWpfDispatcher(window.Dispatcher);
```
