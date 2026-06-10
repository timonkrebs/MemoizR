namespace MemoizR.Tests;

internal static class TestHelpers
{
    // Polls until the reactive graph has settled on the expected state, or the timeout elapses
    // (after which the caller's assertions fail with a meaningful diff). Async, debounced
    // propagation can take a variable number of recompute cycles, so a fixed delay is flaky.
    public static async Task WaitForConvergenceAsync(Func<bool> converged, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!converged() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }

    // Whether `node` is among the live targets of an Observers array -- the graph-membership
    // assertion used by the rewiring and dispose tests.
    public static bool Observes(WeakReference<IMemoizR>[] observers, object node)
    {
        return observers.Any(w => w.TryGetTarget(out var o) && ReferenceEquals(o, node));
    }
}

// Parks a node's recompute at a chosen point so a test can inject a concurrent operation with a
// deterministic interleaving. Usage: the computation awaits PauseIfArmedAsync() after reading
// its source; the test Arm()s the gate, starts the recompute, awaits ReadDone (the computation
// is now parked), injects the racing operation, then calls Proceed().
internal sealed class RecomputeGate
{
    private bool armed;
    // RunContinuationsAsynchronously: completing a gate must not run the other side's
    // continuation inline on the completer's stack, where it could contend the very locks the
    // completer holds.
    private readonly TaskCompletionSource readDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ReadDone => readDone.Task;

    public void Arm() => Volatile.Write(ref armed, true);

    public void Proceed() => proceed.SetResult();

    public async Task PauseIfArmedAsync()
    {
        if (Volatile.Read(ref armed))
        {
            Volatile.Write(ref armed, false);
            readDone.SetResult();
            await proceed.Task;
        }
    }
}
