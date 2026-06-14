namespace MemoizR.Wpf.Tests;

internal static class WpfTestHelpers
{
    // Polls until the reactive graph has settled on the expected state, or the timeout elapses
    // (after which the caller's assertions fail with a meaningful diff). Async, debounced
    // propagation can take a variable number of recompute cycles, so a fixed delay is flaky.
    // Generous default: every reaction action additionally round-trips through the dispatcher
    // queue, which competes with everything else posted to the shared test Application.
    public static async Task WaitForConvergenceAsync(Func<bool> converged, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!converged() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }
}
