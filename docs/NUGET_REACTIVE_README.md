# MemoizR:Reactive: Simplifying Concurret Reactivity in .NET

This package extends the functionality provided by the foundational MemoizR package.

It closely resembles the behavior of signals in solid.js, while also incorporating essential thread-safety features. This implementation draws inspiration from the concepts found in reactively (https://github.com/modderme123/reactively).

Unlike ReactiveX, there's no need to handle subscriptions manually.

## Reactivity
You can use MemoizR.Reactive to create reactive data flows easily:

```csharp
var f = new MemoFactory();
var v1 = f.CreateSignal(1);
var m1 = f.CreateMemoizR(async() => await v1.Get());
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2);
var r1 = f.BuildReaction().CreateReaction(m1, m2, (val1, val2) => val1 + val2);
```

## UI threads

Register the UI thread's SynchronizationContext (from the UI thread) and reactions deliver
their action on it, while the dependencies — passed as separate parameters so they can be
evaluated independently of the action — keep evaluating on the thread pool:

```csharp
// e.g. in a UI app: capture the UI SynchronizationContext on the UI thread
var UISyncContext = SynchronizationContext.Current!;
var f = new MemoFactory();
f.AddSynchronizationContext(UISyncContext);

// m1 and m2 are computed on worker threads; only the action runs on the UI thread.
var r1 = f.BuildReaction().CreateReaction(m1, m2, (val1, val2) => val1 + val2);
```

For WPF, the MemoizR.Wpf package wires this up directly from `Application.Current.Dispatcher`
(callable from any thread): `var f = new MemoFactory().AddWpfDispatcher();`