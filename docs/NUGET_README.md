# MemoizR: Simplifying Concurrent State in .NET

> "The world is still short on languages that deal super elegantly and inherently and intuitively with concurrency" **Mads Torgersen** Lead Designer of C# (https://www.youtube.com/watch?v=Nuw3afaXLUc&t=4402s)

MemoizR is a powerful library that simplifies concurrent state management in .NET. It provides a performant and thread-safe way to handle complex state synchronization across multiple threads, making it ideal for a wide range of applications.

## **Key Features**

* **Dynamic Lazy Memoization:** Calculate values only when needed, avoiding unnecessary computations and optimizing performance.  
* **Declarative Structured Concurrency:** Easily manage complex concurrency scenarios with straightforward configuration, effortless maintenance, robust error handling, and seamless cancellation.  
* **Dependency Graph:** Automatically track dependencies between your data, ensuring that only necessary computations are performed.  
* **Automatic Synchronization:** Work with shared state without the hassle of manual synchronization.  
* **Performance Optimization:** Benefit from memoization for read-heavy scenarios and lazy evaluation for write-heavy scenarios.

## **Benefits**

* **Simplicity:** MemoizR offers a more intuitive and manageable approach to concurrency compared to traditional async/await patterns.  
* **Scalability:** The concurrency model can be extended to distributed setups, similar to the actor model.  
* **Maintainability:** MemoizR helps you write cleaner and more maintainable code, especially when dealing with complex concurrent state.  
* **Performance:** Optimize performance for both read-heavy and write-heavy scenarios.

## **Inspiration**

MemoizR draws inspiration from various sources:

* **Dynamic Lazy Memoization:** Solid and Reactively  
* **Structured Concurrency:** Principles for well-structured concurrent code.

## **Usage**

### **Basic Memoization**

```csharp
var f = new MemoFactory();
var v1 = f.CreateSignal(1);
var m1 = f.CreateMemoizR(async() => await v1.Get());
var m2 = f.CreateMemoizR(async() => await v1.Get() * 2);
var m3 = f.CreateMemoizR(async() => await m1.Get() + await m2.Get());
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

### **Declarative Structured Concurrency**
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
    async _ =>
    {
        await child1.Get();

        // Any group work can kick off other group work.
        await Task.WhenAll(Enumerable.Range(1, 10)
            .Select(x => f.CreateConcurrentMapReduce(
                async c =>
                {
                    await Task.Delay(3000, c.Token);
                    return x;
                }).Get()));
        
        return 4;
    });

var x = await c1.Get();

```

## **Get Started**

Start using MemoizR to simplify and optimize concurrency management in your .NET applications.

## **Try it out\!**

Experiment with MemoizR online: https://dotnetfiddle.net/Widget/EWtptc

Example From: [Khalid Abuhakmeh](https://khalidabuhakmeh.com/memoizr-declarative-structured-concurrency-for-csharp#conclusion)