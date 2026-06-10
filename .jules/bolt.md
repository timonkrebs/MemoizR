## 2024-06-10 - Avoid LINQ `.Any()` on arrays in hot paths
**Learning:** In the `MemoizR` reactive dependency graph update loops (e.g. `UpdateSourceAndObserverLinks`), using `.Any()` on arrays (`Sources`, `Observers`, `CurrentGets`) incurs significant enumerator allocation and interface dispatch overhead compared to a direct array `.Length` property check.
**Action:** Always prefer `array.Length > 0` or `array.Length == 0` over `.Any()` or `!.Any()` for performance critical sections in C#.
