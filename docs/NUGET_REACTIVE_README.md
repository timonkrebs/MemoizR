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
var r1 = f.CreateReaction(m1, m2, (val1, val2) => val1 + val2);
```

```csharp

// The following example uses the TaskScheduler.FromCurrentSynchronizationContext method in a Windows Presentation Foundation (WPF) app to schedule
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskscheduler?view=net-7.0#specifying-a-synchronization-context
var UISyncContext = SynchronizationContext.Current;
var f = new MemoFactory();
f.AddSynchronizationContext(UISyncContext);

var r1 = f.CreateReaction(m1, m2, (val1, val2) => val1 + val2);
```

Reactions can be pinned to any `IExecutor` (the analog of Swift's custom actor executors), per
factory or per builder — e.g. a `DedicatedThreadExecutor`, whose installed
`SynchronizationContext` keeps async continuations on its single thread:

```csharp
using var executor = new DedicatedThreadExecutor();
var f = new MemoFactory().AddExecutor(executor);

var r1 = f.BuildReaction().CreateReaction(m1, val =>
{
    executor.AssertIsolated(); // throws when not running on the executor
});
```