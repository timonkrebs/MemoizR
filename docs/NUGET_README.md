# MemoizR: Simplifying Structured Concurrency in .NET

> "The world is still short on languages that deal super elegantly and inherently and intuitively with concurrency" **Mads Torgersen** Lead Designer of C# (https://www.youtube.com/watch?v=Nuw3afaXLUc&t=4402s)

MemoizR is a powerful Structured Concurrency State implementation designed to simplify and enhance state synchronization across multiple threads in .NET applications. It offers a performant and thread-safe approach to managing concurrency challenges, making it an excellent choice for various scenarios.

## Key Advantages

- **Simplicity and Intuitiveness**: MemoizR aims to provide a straightforward and intuitive way to handle concurrency, avoiding the complexities often associated with async/await patterns (e.g. async void, .Wait, not awaiting everything and even .ConfigureAwait) in C#. It offers a more natural approach to managing asynchronous tasks and multithreading.

- **Scalability**: This concurrency model has the potential for expansion into distributed setups, similar to the actor model. It can help you build scalable and distributed systems with ease.

- **Maintainable Code**: MemoizR is designed to ensure the maintainability of your code, especially when dealing with challenging concurrent state synchronization problems in multi-threaded environments.

- **Performance Optimization**: Even for simple use cases, MemoizR can optimize performance in scenarios where there are more reads than writes (thanks to memoization) or more writes than reads (using lazy evaluation).

## Dynamic Lazy Memoization

With MemoizR, you can create a dependency graph that performs dynamic lazy memoization. This means that values are calculated only when needed and only if they haven't been calculated before (memoization). It also ensures efficient resource utilization and reduces unnecessary calculations (lazy). This implementation draws inspiration from the concepts found in reactively (https://github.com/modderme123/reactively)

## Inspiration

MemoizR draws inspiration from various sources:

- **Dynamic Lazy Memoization**:  (https://github.com/modderme123/reactively)
- **Structured Concurrency**: (https://github.com/StephenCleary/StructuredConcurrency, https://vorpus.org/blog/notes-on-structured-concurrency-or-go-statement-considered-harmful/)

##  Benefits
Here are some key benefits of using MemoizR:

- **Dependency Tracking**: MemoizR automatically tracks dependencies between functions and methods, eliminating the need for manual source listing. This ensures that calculations are triggered only when necessary.

- **Optimization**: MemoizR includes intelligent optimization algorithms. Functions are executed only if required and only once, even in complex dependency networks. This reduces unnecessary computations and enhances efficiency.

## Execution Model: Push-Pull Hybrid

MemoizR utilizes a hybrid push-pull execution model. It pushes notifications of changes down the graph and executes MemoizR elements lazily on demand as they are pulled from the leaves. This approach combines the simplicity of pull systems with the execution efficiency of push systems.

## Example

Here's a simple example of using MemoizR:

```csharp
var f = new MemoFactory();
var v1 = f.CreateSignal(1, "v1");
var m1 = f.CreateMemoizR(async() => await v1.Get(), "m1");
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2, "m2");
var m3 = f.CreateMemoizR(async() => await m1.Get() + await m2.Get(), "m3");

// Get Values
await m3.Get(); // Calculates m1 + 2 * m1 => (1 + 2 * 1) = 3

// Change
await v1.Set(2); // Setting v1 does not trigger the evaluation of the graph
await m3.Get(); // Calculates m1 + 2 * m1 => (1 + 2 * 2) = 6
await m3.Get(); // No operation, result remains 6

await v1.Set(3); // Setting v1 does not trigger the evaluation of the graph
await v1.Set(2); // Setting v1 does not trigger the evaluation of the graph
await m3.Get(); // No operation, result remains 6 (because the last time the graph was evaluated, v1 was already 2)
```

MemoizR can also handle scenarios where the graph is not stable at runtime, making it adaptable to changing dependencies.

```cs
var m3 = f.CreateMemoizR(async() => await v1.Get() ? await m1.Get() : await m2.Get());
```

## Get Started with MemoizR

Start using MemoizR to simplify and optimize concurrency management in your .NET applications. Enjoy a cleaner, and more efficient approach to handling concurrency challenges.