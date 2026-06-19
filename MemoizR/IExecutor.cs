namespace MemoizR;

/// <summary>
/// A seat of execution for reactive side effects -- the analog of Swift's custom actor executors
/// (SE-0392, issue #36). An implementation decides only WHERE enqueued work runs (a UI
/// SynchronizationContext, a dedicated thread, a test scheduler); completion tracking and
/// exception marshalling stay with the library, so the delegate handed to
/// <see cref="Enqueue"/> never throws.
/// </summary>
public interface IExecutor
{
    /// <summary>
    /// Schedules <paramref name="work"/> to run on this executor, fire-and-forget (Swift's
    /// <c>enqueue(job)</c>). The delegate is self-contained: it observes its own exceptions and
    /// carries its own completion signal, so implementations need no result plumbing. Work
    /// enqueued from the executor itself (an async continuation, a nested effect) must also be
    /// accepted -- running it inline is permitted only when that cannot grow the stack unboundedly.
    /// </summary>
    void Enqueue(Action work);

    /// <summary>
    /// Whether the calling code is currently running on this executor. A point-in-time answer
    /// meant for dynamic isolation checks ("am I on the executor?" -- see
    /// <see cref="ExecutorExtensions.AssertIsolated"/>); never a license to skip the hop, and,
    /// like every dynamic isolation primitive here, best-effort for executors whose underlying
    /// context cannot be identified from the running code.
    /// </summary>
    bool IsCurrent { get; }
}

public static class ExecutorExtensions
{
    /// <summary>
    /// Throws when the calling code is not running on <paramref name="executor"/> -- the
    /// executor-flavored <c>preconditionIsolated()</c> (SE-0392/0423 analog), the sibling of
    /// <see cref="MemoFactory.AssertEvaluationIsolated"/>. Call it from side-effecting code that
    /// must only ever touch executor-isolated state (e.g. UI elements) to turn a silent
    /// wrong-thread mutation into an immediate, located failure.
    /// </summary>
    public static void AssertIsolated(this IExecutor executor)
    {
        if (!executor.IsCurrent)
        {
            throw new InvalidOperationException(
                $"This code expected to run on {executor.GetType().Name}, but is executing elsewhere. " +
                "Side effects isolated to an executor must only be touched from work enqueued on it.");
        }
    }
}
