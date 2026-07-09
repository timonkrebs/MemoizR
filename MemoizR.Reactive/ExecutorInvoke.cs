namespace MemoizR.Reactive;

// The one executor-hop protocol, shared by ReactionBase.InvokeExecute (an AdvancedReaction's
// whole async body) and ReactionBuilder.InvokeActionAsync (a composed reaction's already-bound
// UI action). The executor only decides WHERE the work runs (SE-0392 analog); completion and
// exception semantics live here, once, so an IExecutor implementation cannot get them wrong.
internal static class ExecutorInvoke
{
    internal static async Task RunAsync(IExecutor executor, Func<Task> work)
    {
        // RunContinuationsAsynchronously: completing the TCS must not run the rest of the
        // update pipeline (link rewiring, state commit, lock releases) inline inside the
        // executor's slot -- that work belongs to the update's own flow, and running it on
        // e.g. a UI thread inside the enqueued callback both blocks that thread and exposes
        // the pipeline to whatever exception handling wraps the executor's callbacks.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async void RunOnExecutor()
        {
            // Complete the TCS exactly once: SetResult after SetException would throw
            // InvalidOperationException out of this async void and crash the process via the
            // executor instead of faulting the awaited task. This wrapper is also what makes
            // the IExecutor contract hold ("the enqueued delegate never throws"); a work
            // delegate that throws synchronously is caught here too, before any await.
            try
            {
                await work();
                tcs.SetResult();
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }

        // Carry the reaction's Context scope across the executor hop. The scope lives in an
        // AsyncLocal, which flows only through ExecutionContext; an executor that runs the
        // callback on its own thread without flowing it (e.g. DedicatedThreadExecutor) would
        // otherwise drop the scope, so the work's dependency reads would see no current
        // reaction, capture nothing, and never wire sources to re-trigger on. Capturing here
        // makes this independent of whether a given IExecutor flows ExecutionContext itself.
        var executionContext = ExecutionContext.Capture();
        Action callback = executionContext is null
            ? RunOnExecutor
            : () => ExecutionContext.Run(executionContext, static state => ((Action)state!)(), (Action)RunOnExecutor);

        try
        {
            executor.Enqueue(callback);
        }
        catch (Exception e)
        {
            // A custom IExecutor may reject scheduling (e.g. disposed): fault the awaited
            // task so the failure flows through the update pipeline rather than throwing
            // synchronously past the TCS.
            tcs.TrySetException(e);
        }

        await tcs.Task;
    }
}
