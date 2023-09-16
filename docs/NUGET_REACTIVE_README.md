This package builds on the functionality of the base package MemoizR

```cs
  /*
     Initialize Graph without evaluation
        v1
        | \ 
       m1  m2
         \ |
          r1
  */
var f = new MemoFactory();
var v1 = f.CreateSignal(1, "v1");
var m1 = f.CreateMemoizR(() => v1.Get(), "m1");
var m2 = f.CreateMemoizR(() => v1.Get() * 2, "m2");
var r1 = f.CreateReaction(() => m1.Get() + m2.Get(), "r1");
var r2 = f.CreateReaction(() => v1.Get() + 1, "r2");
```

This behaves mostly the same as signals in solid.js (with added thread savety).
The implementation is based on Reactively https://github.com/modderme123/reactively which is the first draft for the reactivity model in Solid 2.0

Every time a change is made to the signal it runs the reactions that depend on it.
