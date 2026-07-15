namespace MemoizR.Distributed;

// Runs work on a fresh top-level flow, never inheriting the caller's ExecutionContext. The
// callers are inside a reaction evaluation, and the context lock's holdership is AsyncLocal
// state that would otherwise travel into the detached task: its lock acquisitions would be
// refused as recursive inside the reaction's upgradeable scope -- or worse, granted as
// same-scope recursion CONCURRENTLY with the still-running reaction, the corruption the
// reaction machinery itself guards against with fresh scopes. A suppressed flow simply queues
// behind the reaction's lock, like any transport-thread work.
internal static class DetachedFlow
{
    internal static void Run(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            Start();
            return;
        }
        using (ExecutionContext.SuppressFlow())
        {
            Start();
        }

        void Start() => _ = Task.Run(work);
    }
}
