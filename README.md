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

### Reactivity
```cs
var f = new MemoFactory();
var v1 = f.CreateSignal(1);
var m1 = f.CreateMemoizR(async() => await v1.Get());
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2);
var r1 = f.CreateReaction(m1, m2, (val1, val2) => val1 + val2);
```

Try it out!https:
Experiment with MemoizR online: https://dotnetfiddle.net/Widget/EWtptc

Example From: [Khalid Abuhakmeh](https://khalidabuhakmeh.com/memoizr-declarative-structured-concurrency-for-csharp#conclusion)