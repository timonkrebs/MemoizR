namespace MemoizR.Distributed;

// Runs bridge work on a fresh top-level flow, never inheriting the caller's ExecutionContext.
// The callers are inside a reaction evaluation, and the context lock's holdership is AsyncLocal
// state that would otherwise travel into the detached task: its lock acquisitions would be
// refused as recursive inside the reaction's upgradeable scope -- or worse, granted as
// same-scope recursion CONCURRENTLY with the still-running reaction, the corruption the
// reaction machinery itself guards against with fresh scopes. On its own flow the work takes
// ordinary, never recursive, lock acquisitions -- like any transport-thread work. Faults go to
// the caller's sink: detached work has no one to throw to.
internal static class DetachedFlow
{
    internal static void Run(Func<Task> work, Action<Exception> onError)
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

        void Start() => _ = Task.Run(() => GuardAsync(work, onError));
    }

    private static async Task GuardAsync(Func<Task> work, Action<Exception> onError)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }
}
