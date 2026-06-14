## 2026-06-13 - [Fix Silent Swallowing of Exceptions During Resource Disposal]
**Vulnerability:** Structured concurrency `DisposeResources` ignored/swallowed all exceptions thrown by resource `Dispose()` or `DisposeAsync()` calls instead of aggregating them.
**Learning:** Silently failing to clean up resources (which could result in connection leaks, unreleased locks, file descriptor leaks) poses a Denial of Service or security impact depending on what resource failed to clean up.
**Prevention:** Always accumulate disposal exceptions during teardown and throw an `AggregateException` to ensure developers are aware of cleanup failures and can handle them appropriately.
