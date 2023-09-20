With this package it is possible to build a dependency graph that does dynamic lazy memoization. 
It calculates only the values that are needed and only when they are not already calculated (memoization).

Initial inspiration by https://github.com/modderme123/reactively
Yet not primarily by the reactivity but by the unique idea of dynamic lazy memoization.

## Vs. Manual Caching
You could build caching yourself of course, but using MemoizR has several advantages.

- First, as you've seen, it's very simple.
- Second, MemoizR functions and methods automatically track their sources. Many approaches to caching require that the programmer manually list sources. That's not just more effort to maintain, a static source list is apt to include sources that are not needed every time, which means your MemoizR functions are apt to rerun unnecessarily.
- Third, MemoizR dependency tracking extends beyond class/component boundaries, so the benefits of clever caching and smart recalculation extends across modules.
- Finally, MemoizR includes some clever global optimization algorithms. A MemoizR function is run only if needed and only runs once. Furthermore, even deep and complicated networks of dependencies are analyzed efficiently in linear time. Without something like MemoizR, it's easy to end up with O(n log n) searches if every use of a MemoizR function needs to check every dependency, or every change needs to notify every dependent.

## Execution model

The set of MemoizR elements and their source dependencies forms a directed (and usually acyclic) graph. Conceptually, MemoizR changes enter at the roots of the graph and propagate to the leaves.

But that doesn’t mean the MemoizR system needs to respond to a change at the root by immediately deeply traversing the tree and re-executing all of the related MemoizR elements. What if the user has made changes to two roots? We might unnecessarily execute elements twice, which is inefficient. What if the user doesn’t consume a MemoizR leaf element before a new change comes around? Why bother executing that element if it’s not used? If two changes are in flight through the execution system at the same time, might user code see an unexpected mixed state (this is called a ‘glitch’)?

These are the questions the MemoizR execution system needs to address.

Push systems emphasize pushing changes from the roots down to the leaves. Push algorithms are fast for the framework to manage but can push changes even through unused parts of the graph, which wastes time on unused user computations and may surprise the user. For efficiency, push systems typically expect the user to specify a 'batch' of changes to push at once.

Pull systems emphasize traversing the graph in reverse order, from user consumed elements up towards roots. Pull systems have a simple developer experience and don’t require explicit batching. But pull systems are apt to traverse the tree too often. Each leaf element needs to traverse all the way up the tree to detect changes, potentially resulting in many extra traversals.

MemoizR is a hybrid push-pull system. It pushes dirty notifications down the graph, and then executes MemoizR elements lazily on demand as they are pulled from leaves. This costs the framework some bookkeeping and an extra traversal of its internal graph. But the developer wins by getting the simplicity of a pull system and the most of the execution efficiency of a push system.


```cs
  /*
     Initialize Graph without evaluation
        v1
        | \ 
       m1  m2
         \ |
          m3
  */
var f = new MemoFactory();
var v1 = f.CreateSignal(1, "v1");
var m1 = f.CreateMemoizR(() => v1.Get(), "m1");
var m2 = f.CreateMemoizR(() => v1.Get() * 2, "m2");
var m3 = f.CreateMemoizR(() => m1.Get() + m2.Get(), "m3");

// Get Values
m3.Get(); // calc  m1 + 2 * m1 => ( 1 + 2 * 1 ) = 3

// Change
v1.Set(2); // Set is not triggering evaluation of graph
m3.Get(); // calc  m1 + 2 * m1 => ( 1 + 2 * 1 ) = 6
m3.Get(); // noop => 6

v1.Set(3); // Set is not triggering evaluation of graph
v1.Set(2); // Set is not triggering evaluation of graph
m3.Get(); // noop => 6 ( because the last time the Graph was evaluated v1 was already 2 )
```

It also works if the Graph is not stable at runtime. MemoizR can handle if the Graph changes like:
```cs
var m3 = f.CreateMemoizR(() => v1.Get() ? m1.Get() : m2.Get());
```