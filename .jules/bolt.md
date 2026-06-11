## 2024-05-24 - Remove LINQ and Spread Operators in Hot Paths

**Learning:** In highly traversed graph propagation paths (like `UpdateSourceAndObserverLinks` and `RemoveParentObservers` in `MemoizR`), using LINQ methods like `.Where()` and `.Take()` along with C# 12 collection expressions (`[.. x]`) incurs significant allocation overhead. These allocations (delegates, enumerators, arrays) dominate the execution time compared to raw array operations.

**Action:** Prefer manual `for` loops for filtering and `Array.Copy` for slicing and concatenating arrays in core reactive data structure algorithms. This can yield performance improvements of 3x to 5x by avoiding garbage collection and minimizing interface dispatch overhead.