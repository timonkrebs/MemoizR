<h1 align="center">MemoizR</h1>

<p align="center">
  <img src="docs/MemoizR-Small.png" alt="MemoizR-logo" width="120px" height="120px"/>
  <br>
  <em>MemoizR is a powerful Declarative Structured Concurrency model</em>
  <br>
</p>



![CI](https://github.com/timonkrebs/MemoizR/workflows/.NET/badge.svg)
[![NuGet](https://img.shields.io/nuget/dt/memoizr.svg)](https://www.nuget.org/packages/memoizr) 
[![NuGet](https://img.shields.io/nuget/vpre/memoizr.svg)](https://www.nuget.org/packages/memoizr)

> "The world is still short on languages that deal super elegantly and inherently and intuitively with concurrency" **Mads Torgersen** Lead Designer of C# (https://www.youtube.com/watch?v=Nuw3afaXLUc&t=4402s)

MemoizR is a Declarative Structured Concurrency implementation for .NET that simplifies and enhances errorhandling, maintainability and state synchronization across multiple threads. It provides a maintainable and efficient way to manage concurrency, making it suitable for both simple and complex multi-threaded scenarios.

> the dynamic structured concurrency part is still being worked on.

Inspired From **Stephen Cleary — Asynchronous streams** https://youtu.be/-Tq4wLyen7Q?si=-dR4cHAutod6i0VG&t=715
| compared to      | which is     | MemoizR/Signals |
| --------         | -------      | -------         |
| IEnumerable      | synchronous  | asynchronous    |
| Task             | single value | multi value     |
| Observable       | push based   | push-pull       |
| IAsyncEnumerable | pull based   | push-pull       |

## Key Features
- **Dynamic Lazy Memoization**: MemoizR introduces the concept of dynamic lazy memoization, allowing you to calculate values only when they are needed and not already calculated.
- **Declarative Structured Concurrency**: This is the most innovation part of this library. it enables easy setup, maintainability, error handling and cancelation of complex concurrency usecases.
- **Dependency Graph**: It enables you to build a dependency graph of your data, ensuring that only necessary computations are performed.
- **Automatic Synchronization**: MemoizR handles synchronization, making it easy to work with hard-to-concurrently-synchronize state.
- **Performance Optimization**: Depending on your use case, MemoizR can optimize performance for scenarios with more reads than writes (thanks to memoization) or more writes than reads (using lazy evaluation).

## Inspiration
- This implementation draws inspiration from the concepts found in reactively (https://github.com/modderme123/reactively), primarily the concept of dynamic lazy memoization.
- Also from various other sources, VHDL for synchronization, ReactiveX (https://reactivex.io/), structured concurrency (https://github.com/StephenCleary/StructuredConcurrency, https://vorpus.org/blog/notes-on-structured-concurrency-or-go-statement-considered-harmful/) and many more.
- Special thanks to @mfp22 for the idea of signal operators, which got me started with this library.

## Similarities to ReactiveX Dataflow and Advantages of MemoizR

MemoizR shares some similarities with the Dataflow library in terms of handling concurrency and managing data flows. 
However, MemoizR offers several advantages, such as implicit Join and LinkTo, which make it a powerful choice for managing concurrent operations and reactive data flows.

### Dataflow Paradigm

Both MemoizR and the Dataflow library are designed to handle concurrent operations and data flow in a structured manner. 
They provide abstractions for defining tasks, dependencies, and synchronization, making it easier to manage complex concurrency scenarios.

### Implicit Join

One key advantage of MemoizR is its implicit Join mechanism. In Dataflow, you often need to explicitly define Join blocks to synchronize and combine data from multiple sources. In MemoizR, this synchronization happens automatically when you define dependencies between signals, memos, and reactions. For example:

```csharp
// Setup
var f = new MemoFactory();
var v1 = f.CreateSignal(1, "v1");
var m1 = f.CreateMemoizR(async() => await v1.Get(), "m1");
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2, "m2");
var m3 = f.CreateMemoizR(async() => await m1.Get() + await m2.Get(), "m3");
```
In this code, r1 automatically depends on the results of m1 and m2, and their values are synchronized without the need for explicit Join blocks.

### Implicit LinkTo
MemoizR also provides implicit LinkTo functionality. While in Dataflow, you typically use the LinkTo method to connect dataflow blocks, MemoizR handles the linking of dependencies automatically based on your code's structure. This simplifies the setup and maintenance of data flow relationships.

## Usage

```cs
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

MemoizR can also handle dynamic changes in the graph, making it suitable for scenarios where the structure of the dependency graph may change at runtime.

```cs
var m3 = f.CreateMemoizR(async() => await v1.Get() ? await m1.Get() : await m2.Get());
```

## Declarative Structured Concurrency
MemoizR's declarative structured concurrency model enhances maintainability, error handling, and cancellation of complex concurrency use cases. It allows you to set up and manage concurrency in a clear and structured way, making your code easier to understand and maintain.

In summary, MemoizR offers a powerful and intuitive approach to managing concurrency and reactive data flows, with features like implicit Join and LinkTo that simplify your code and improve maintainability. It also draws inspiration from ReactiveX, making it a versatile choice for reactive programming scenarios.

## Reactivity
You can use MemoizR to create reactive data flows easily:

```csharp
var f = new ReactiveMemoFactory();
var v1 = f.CreateSignal(1, "v1");
var m1 = f.CreateMemoizR(async() => await v1.Get(), "m1");
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2, "m2");
var r1 = f.CreateReaction(async() => await m1.Get() + await m2.Get(), "r1");
```
