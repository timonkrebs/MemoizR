# MemoizR

![CI](https://github.com/timonkrebs/MemoizR/workflows/.NET/badge.svg)
[![NuGet](https://img.shields.io/nuget/dt/memoizr.svg)](https://www.nuget.org/packages/memoizr) 
[![NuGet](https://img.shields.io/nuget/vpre/memoizr.svg)](https://www.nuget.org/packages/memoizr)

> "The world is still short on languages that deal super elegantly and inherently and intuitively with concurrency" Mads Torgersen (https://www.youtube.com/watch?v=Nuw3afaXLUc&t=4402s)

MemoizR is a powerful concurrency model implementation for .NET that simplifies and enhances state synchronization across multiple threads. It provides a thread-safe and efficient way to manage concurrency, making it suitable for both simple and complex multi-threaded scenarios.

## Key Features
- **Dynamic Lazy Memoization**: MemoizR introduces the concept of dynamic lazy memoization, allowing you to calculate values only when they are needed and not already calculated. 
- **Dependency Graph**: It enables you to build a dependency graph of your data, ensuring that only necessary computations are performed.
- **Automatic Synchronization**: MemoizR handles synchronization, making it easy to work with hard-to-concurrently-synchronize state.
- **Performance Optimization**: Depending on your use case, MemoizR can optimize performance for scenarios with more reads than writes (thanks to memoization) or more writes than reads (using lazy evaluation).

## Inspiration
- This implementation draws inspiration from the concepts found in reactively (https://github.com/modderme123/reactively), primarily the concept of dynamic lazy memoization.
- Also from various other sources, VHDL for synchronization, ReactiveX (https://reactivex.io/), structured concurrency (https://github.com/apple/swift-evolution/blob/main/proposals/0304-structured-concurrency.md) and many more.
- Special thanks to @mfp22 for the idea of signal operators, which got me started with this library.

## Getting Started
Here's a basic example of how to use MemoizR:

```csharp
var f = new MemoFactory();
var v1 = f.CreateSignal(1, "v1");
var m1 = f.CreateMemoizR(() => v1.Get(), "m1");
var m2 = f.CreateMemoizR(() => v1.Get() * 2, "m2");
var m3 = f.CreateMemoizR(() => m1.Get() + m2.Get(), "m3");

// Get Values
m3.Get(); // Calculates m1 + 2 * m1 => (1 + 2 * 1) = 3

// Change
Task.Run(() => v1.Set(2));
// Synchronization is handled by MemoizR
m3.Get(); // Calculates m1 + 2 * m1 => (1 + 2 * 2) = 6
m3.Get(); // No operation, result is still 6

v1.Set(3); // Setting v1 does not trigger evaluation of the graph
v1.Set(2); // Setting v1 does not trigger evaluation of the graph
m3.Get(); // No operation, result is still 6 (because the last time the graph was evaluated, v1 was already 2)
```

MemoizR can also handle dynamic changes in the graph, making it suitable for scenarios where the structure of the dependency graph may change at runtime.

```cs
var m3 = f.CreateMemoizR(() => v1.Get() ? m1.Get() : m2.Get());
```

## Reactivity
You can use MemoizR to create reactive data flows easily:

```csharp
var f = new MemoFactory();
var v1 = f.CreateSignal(1, "v1");
var m1 = f.CreateMemoizR(() => v1.Get(), "m1");
var m2 = f.CreateMemoizR(() => v1.Get() * 2, "m2");
var r1 = f.CreateReaction(() => m1.Get() + m2.Get(), "r1");
```