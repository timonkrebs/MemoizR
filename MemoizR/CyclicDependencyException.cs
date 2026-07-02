namespace MemoizR;

/// <summary>
/// Thrown when a read closes a dependency cycle: the node being read is already evaluating, and
/// the read is nested inside that very evaluation (actor engine, detected via the evaluation
/// chain). A distinct type -- and a subclass of <see cref="InvalidOperationException"/>, so
/// existing handlers keep working -- because the engine itself must be able to tell a cycle
/// from an ordinary faulted computation: a parent scan converts arbitrary parent faults into
/// "stay CacheCheck and retry", but a cycle is a structural error that must surface to the
/// caller, not hide behind a stale value.
/// </summary>
public sealed class CyclicDependencyException : InvalidOperationException
{
    internal CyclicDependencyException()
        : base("Cyclic behavior detected")
    {
    }
}
