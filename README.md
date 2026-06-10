<h1 align="center">MemoizR</h1>

<p align="center">
  <img src="docs/MemoizR.png" alt="MemoizR-logo" width="120px" height="120px"/>
  <br>
  <em>Streamlined Concurrency</em>
  <br>
</p>


[![.NET](https://github.com/timonkrebs/MemoizR/actions/workflows/dotnet.yml/badge.svg)](https://github.com/timonkrebs/MemoizR/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/dt/memoizr.svg)](https://www.nuget.org/packages/memoizr) 
[![NuGet](https://img.shields.io/nuget/vpre/memoizr.svg)](https://www.nuget.org/packages/memoizr)

> "The world is still short on languages that deal super elegantly and inherently and intuitively with concurrency" **Mads Torgersen** Lead Designer of C# (https://www.youtube.com/watch?v=Nuw3afaXLUc&t=4402s)

MemoizR is a library for .NET that brings the power of Dynamic Lazy Memoization and Declarative Structured Concurrency to your fingertips. It streamlines complex multi-threaded scenarios, making them easier to write, maintain, and reason about.

Inspired From **Stephen Cleary — Asynchronous streams** https://www.youtube.com/watch?v=-Tq4wLyen7Q&t=706s
| compared to      | which is     | MemoizR/Signals |
| --------         | -------      | -------         |
| IEnumerable      | synchronous  | asynchronous    |
| Task             | single value | multi value     |
| Observable       | push based   | push-pull       |
| IAsyncEnumerable | pull based   | push-pull       |

## Key Features
- **Dynamic Lazy Memoization**: Calculate values only when needed, avoiding unnecessary computations and optimizing performance.
- **Declarative Structured Concurrency**: Easily manage complex concurrency scenarios with straightforward configuration, effortless maintenance, robust error handling, and seamless cancellation.
- **Dependency Graph**:Automatically track dependencies between your data, ensuring that only necessary computations are performed.
- **Automatic Synchronization**: Work with shared state without the hassle of manual synchronization.
- **Performance Optimization**: Benefit from memoization for read-heavy scenarios and lazy evaluation for write-heavy scenarios.
Inspiration
## MemoizR draws inspiration from various sources

- **Reactively and Solid**: Dynamic lazy memoization concepts.
- **VHDL**: Synchronization mechanisms.
- **ReactiveX**: Reactive programming paradigms.
- **Structured Concurrency**: Principles for well-structured concurrent code.
Special thanks to @mfp22 for the idea of signal operators!

## Advantages over ReactiveX and Dataflow

MemoizR offers several advantages over traditional concurrency libraries:

### Implicit Subscription Handling
No need to manage subscriptions manually; MemoizR automatically tracks and synchronizes dependencies.
Implicit LinkTo: Dependencies are automatically linked based on your code's structure, simplifying data flow setup.
Simplified Error Handling: Structured concurrency makes error handling more robust and easier to reason about.

## Usage

### Basic Memoization

```cs
// Setup
var f = new MemoFactory();
var v1 = f.CreateSignal(1);
var m1 = f.CreateMemoizR(async() => await v1.Get());
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2);
var m3 = f.CreateMemoizR(async() => await m1.Get() + await m2.Get());

// Get Value manually
await m3.Get(); // Calculates m1 + 2 * m1 => (1 + 2 * 1) = 3

// Change
await Task.Run(async () => await v1.Set(2));
// Synchronization is handled by MemoizR
await m3.Get(); // Calculates m1 + 2 * m1 => (1 + 2 * 2) = 6
await m3.Get(); // No operation, result is still 6

await v1.Set(3); // Setting v1 does not trigger evaluation of the graph
await v1.Set(2); // Setting v1 does not trigger evaluation of the graph
await m3.Get(); // No operation, result is still 6 (because the last time the graph was evaluated, v1 was already 2)
```

### Dynamic Graphs
MemoizR can handle dynamic changes in the dependency graph:

```cs
var m3 = f.CreateMemoizR(async() => await v1.Get() ? await m1.Get() : await m2.Get());
```

### Declarative Structured Concurrency
```cs
var f = new MemoFactory("DSC");

var child1 = f.CreateConcurrentMapReduce(
    async c =>
    {
        await Task.Delay(3000, c.Token);
        return 3;
    });

// all tasks get canceled if one fails
var c1 = f.CreateConcurrentMapReduce(
    async c =>
    {
        await child1.Get();
        return 4;
    });

var x = await c1.Get();
```

### Resources

A concurrent job can own resources. These resources will be disposed by the job after all its work is done.

```cs
var groupTask = f.CreateConcurrentMapReduce(async group =>
{
    group.AddResource(myDisposableResource);

    return await myDisposableResource.DoWorkAsync(group.Token);
});
await groupTask.Get(); // First, waits for all tasks to complete; then, disposes myDisposableResource.
```

All exceptions raised by disposal of any resource are ignored.

### Reactivity
```cs
var f = new MemoFactory();
var v1 = f.CreateSignal(1);
var m1 = f.CreateMemoizR(async() => await v1.Get());
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2);
var r1 = f.CreateReaction(m1, m2, (val1, val2) => val1 + val2);
```

### Data-race safety (strict mode)

MemoizR publishes value *references* tear-free across concurrent flows, but only an immutable
(or thread-safe) type makes the object behind the reference safe to share. Strict mode — the
runtime analog of Swift's `Sendable` checking — validates this at node creation:

```cs
var f = new MemoFactory("strict", MemoFactoryOptions.StrictSendableChecks);

record Person(string Name, int Age);          // init-only members => Sendable
var p = f.CreateSignal(new Person("Ada", 36)); // ok
var xs = f.CreateSignal(ImmutableArray.Create(1, 2, 3)); // ok

f.CreateSignal(new List<int>()); // throws: List<int> is not Sendable
                                 // (writable instance field '_items')
```

Types the structural check cannot prove (internally synchronized ones) can opt in with
`[Sendable]`, the analog of Swift's `@unchecked Sendable`. Code that must only run inside a
serialized graph evaluation can assert it dynamically — the `preconditionIsolated()` analog:

```cs
f.AssertEvaluationIsolated(); // throws outside a Get/Set/recompute/reaction update
```

The same discipline is checked at **build time** by analyzers bundled in the NuGet package
(severity configurable per rule via `.editorconfig`):

| Rule | Flags |
|------|-------|
| `MZR001` | A non-Sendable value type at a `Create*` call — the compile-time mirror of strict mode |
| `MZR002` | A computation writing captured locals, fields, or statics — lift that state into a `Signal` |
| `MZR003` | `Signal.Set` inside a computation, which throws `InvalidOperationException` at runtime |

Reaction side effects can be pinned to an **executor** — the analog of Swift's custom actor
executors (SE-0392). `AddSynchronizationContext(uiContext)` covers UI threads;
`AddExecutor(new DedicatedThreadExecutor())` gives a single-threaded isolation seat whose
installed `SynchronizationContext` keeps async continuations on its thread; any custom
`IExecutor` works, per factory or per `BuildReaction()`:

```cs
using var executor = new DedicatedThreadExecutor();
var f = new MemoFactory().AddExecutor(executor);
var r = f.BuildReaction().CreateReaction(m1, v =>
{
    executor.AssertIsolated(); // the executor-flavored preconditionIsolated()
    // touch executor-isolated state safely
});
```

See [ADR 0003](docs/adr/0003-sendable-checking-and-isolation-assertions.md) (runtime layer),
[ADR 0004](docs/adr/0004-compile-time-data-race-diagnostics.md) (analyzers), and
[ADR 0005](docs/adr/0005-custom-executors.md) (executors) for the design and its limits.

Try it out!https:
Experiment with MemoizR online: https://dotnetfiddle.net/Widget/EWtptc

Example From: [Khalid Abuhakmeh](https://khalidabuhakmeh.com/memoizr-declarative-structured-concurrency-for-csharp#conclusion)

## Testing

Run the test suite with `dotnet test`. Thread interleavings of the locking code are explored
systematically with [Microsoft Coyote](https://microsoft.github.io/coyote/); see
[docs/Coyote.md](docs/Coyote.md) for how to rewrite the assemblies and run the Coyote test.