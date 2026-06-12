## 2024-05-15 - Array operations with WeakReferences
**Learning:** When optimizing array manipulations involving `WeakReference`s, avoid two-pass algorithms (e.g., counting then allocating) because the garbage collector can collect targets between the passes, resulting in invalid counts and subsequent `NullReferenceException`s.
**Action:** Use a single-pass iteration with a temporary array sized to the maximum possible length, and truncate it afterward (`Array.Resize` or `Array.Copy`).
