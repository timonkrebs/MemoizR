With this package it is possible to build a dependency graph that does dynamic lazy memoization. 
It calculates only the values that are needed and also only when they are not already calculated (memoization).

```cs
  /*
     Initialize Graph without evaluation
        v1
        | \ 
       m1  m2
         \ |
          m3
  */
var v1 = new MemoSetR<int>(1, "v1");
var m1 = new MemoizR<int>(() => v1.Get(), "m1");
var m2 = new MemoizR<int>(() => v1.Get() * 2, "m2");
var m3 = new MemoizR<int>(() => m1.Get() + m2.Get(), "m3");

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
var m3 = new MemoizR<int>(() => v1.Get() ? m1.Get() : m2.Get());
```