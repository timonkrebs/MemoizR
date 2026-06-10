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

A custom label or debounce time goes through the builder:
`f.BuildReaction("MyReaction").AddDebounceTime(TimeSpan.FromMilliseconds(16)).CreateReaction(...)`.

## Testing time-dependent reactions
The debounce delay of reactions runs on a `TimeProvider` (default: `TimeProvider.System`). Inject a fake provider, e.g. `Microsoft.Extensions.Time.Testing.FakeTimeProvider`, to control when debounce windows elapse instead of waiting out wall-clock time:

```csharp
var timeProvider = new FakeTimeProvider();
var f = new MemoFactory().AddTimeProvider(timeProvider); // applies to reactions built afterwards

var r1 = f.BuildReaction()
    .AddDebounceTime(TimeSpan.FromSeconds(1))
    .CreateReaction(m1, val => { /* side effect */ });

await v1.Set(2);                                // schedules the debounced update
timeProvider.Advance(TimeSpan.FromSeconds(1));  // releases it deterministically
```

The provider can also be set per reaction on the builder: `f.BuildReaction().AddTimeProvider(timeProvider)`.
