# MemoizR:Reactive: Simplifying Concurret Reactivity in .NET

This package extends the functionality provided by the foundational MemoizR package.

It closely resembles the behavior of signals in solid.js, while also incorporating essential thread-safety features. This implementation draws inspiration from the concepts found in reactively (https://github.com/modderme123/reactively).

Unlike ReactiveX, there's no need to handle subscriptions manually.

## Reactivity
You can use MemoizR.Reactive to create reactive data flows easily:

```csharp
var f = new ReactiveMemoFactory();
var v1 = f.CreateSignal(1);
var m1 = f.CreateMemoizR(async() => await v1.Get());
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2);
var r1 = f.CreateReaction(async() => await m1.Get() + await m2.Get());
```

```csharp
var f = new MemoFactory();
var v1 = f.CreateSignal(1);
var m1 = f.CreateMemoizR(async() => await v1.Get());
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2);


// The following example uses the TaskScheduler.FromCurrentSynchronizationContext method in a Windows Presentation Foundation (WPF) app to schedule
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskscheduler?view=net-7.0#specifying-a-synchronization-context
var UISyncContext = TaskScheduler.FromCurrentSynchronizationContext();
var fr = new ReactiveMemoFactory(UISyncContext);
var r1 = fr.CreateReaction(async() => await m1.Get() + await m2.Get());
var r1 = fr.CreateReaction(async() => await m1.Get() + await m2.Get());
```