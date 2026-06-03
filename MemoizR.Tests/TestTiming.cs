using System.Runtime.CompilerServices;

namespace MemoizR.Tests;

/// <summary>
/// Timing helpers for the reactive tests.
///
/// The reactive pipeline updates observers fire-and-forget through a debounced
/// <c>Task.Delay(...).ContinueWith(...)</c> (see <c>ReactionBase.Stale</c>), so a
/// <c>Signal.Set</c>/<c>Get</c> returns <i>before</i> the reactions it triggered have re-run.
/// There is no completion signal to await, so tests historically slept a fixed
/// <c>await Task.Delay(100)</c> and then asserted an exact state. On a loaded CI runner the
/// reaction continuations are scheduled late, the fixed sleep expires first, and the assertion
/// observes a stale/under-counted value — i.e. the suite is flaky for timing reasons, not logic.
///
/// <see cref="Until"/> converges as soon as the expected state is observed and only spends the
/// full budget when the machine is genuinely slow. That keeps the tests fast on a developer
/// machine while making them robust on constrained CI agents (arm/macOS, 2 cores).
/// </summary>
internal static class Eventually
{
    /// <summary>Generous upper bound: a deadlock/hang still surfaces, a slow runner does not flake.</summary>
    public const int DefaultTimeoutMs = 15_000;

    /// <summary>
    /// Polls <paramref name="assertion"/> until it stops throwing, or rethrows its last failure
    /// once <paramref name="timeoutMs"/> elapses. Pass the whole assertion block so the original
    /// xUnit failure message is preserved on a genuine (non-timing) regression.
    /// </summary>
    public static async Task Until(Action assertion, int timeoutMs = DefaultTimeoutMs, int pollMs = 5)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                assertion();
                return;
            }
            catch when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(pollMs);
            }
        }
    }

    /// <summary>Async overload, for assertion blocks that need to <c>await</c> (e.g. <c>await m.Get()</c>).</summary>
    public static async Task Until(Func<Task> assertion, int timeoutMs = DefaultTimeoutMs, int pollMs = 5)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                await assertion();
                return;
            }
            catch when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(pollMs);
            }
        }
    }

    /// <summary>Boolean-condition overload for waits that are not expressed as assertions.</summary>
    public static Task Until(Func<bool> condition, int timeoutMs = DefaultTimeoutMs, int pollMs = 5)
        => Until(() => Assert.True(condition(), "Condition was not met within the timeout."), timeoutMs, pollMs);
}

internal static class TestThreadPool
{
    /// <summary>
    /// Several tests fan out hundreds-to-thousands of fire-and-forget tasks and then wait for the
    /// reaction pipeline to drain. With a cold thread pool the runtime adds worker threads only
    /// slowly (hill-climbing ~1/500ms), so those reaction continuations queue behind the storm and
    /// miss their window. Pre-growing the pool removes that cold-start starvation. Harmless on a
    /// fast machine: the extra minimum threads are idle when not needed.
    /// </summary>
    [ModuleInitializer]
    public static void Warmup()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var target = Math.Max(Environment.ProcessorCount * 8, 64);
        ThreadPool.SetMinThreads(Math.Max(worker, target), Math.Max(io, target));
    }
}
