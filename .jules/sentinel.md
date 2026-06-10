## 2026-06-10 - Replace Weak Random Number Generation with Guid
**Vulnerability:** Weak random number generation (`Random.Shared.NextDouble()`) was used to generate unique identifiers for asynchronous execution and locking scopes. This is predictable and lacks proper collision resistance.
**Learning:** In highly concurrent code using `AsyncLocal` contexts, predictable identifiers can theoretically allow cross-talk or lock-hijacking if scopes collide or are successfully guessed.
**Prevention:** Always use a cryptographically strong unique identifier such as `Guid.NewGuid()` when needing uniqueness guarantees in asynchronous flow scopes, rather than `Random.Shared.NextDouble()`.
