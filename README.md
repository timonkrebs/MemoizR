# MemoizR

Initial inspiration by https://github.com/modderme123/reactively
Yet not primarily by the reactivity but by the unique idea of dynamic lazy memoization.

The synchronisation for the concurency model ist inspired by VHDL.

Some inspirations also come from S.js (https://github.com/adamhaile/S) and the JS eventloop.

## Value

The value that this package can bring is a simple concurency model.

It aims to be simpler than the current async await behaviour in C# where e.g. async void, .wait, ConfigureAwait or not awaiting everything can lead to problems. This model has also the potential to be expanded to work also in a distributed setup like the actor model.

Even for simple usecases it can optimize performance if:
- there are more reads than writes: The memoization leads to perf gains.
- there are more writes than writes: The lazy evaluation leads to perf gains.

With this package it is possible to build a dependency graph that does dynamic lazy memoization. 
It calculates only the values that are needed and also only when they are not already calculated (memoization).


## Examples
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
var v1 = f.CreateMemoSetR(1, "v1");
var m1 = f.CreateMemoizR(() => v1.Get(), "m1");
var m2 = f.CreateMemoizR(() => v1.Get() * 2, "m2");
var m3 = f.CreateMemoizR(() => m1.Get() + m2.Get(), "m3");

// Get Values
m3.Get(); // calc  m1 + 2 * m1 => ( 1 + 2 * 1 ) = 3

// Change
Task.Run(() => v1.Set(2) );
// Synchronization is handled by MemoizR
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

## Publish to nuget:
MemoizR> dotnet pack --configuration Release /p:ContinuousIntegrationBuild=true -p:PackageVersion=0.0.3-rc1
