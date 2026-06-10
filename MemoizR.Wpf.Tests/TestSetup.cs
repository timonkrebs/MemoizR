using System.Runtime.CompilerServices;

namespace MemoizR.Wpf.Tests;

internal static class TestSetup
{
    // Same rationale as MemoizR.Tests.TestSetup: the convergence polls and dispatcher
    // round-trips run against tight [Fact(Timeout=...)] budgets, and the default thread pool
    // injects new workers only ~1-2 per second, which can stall Task.Delay continuations on
    // few-core CI runners. Pre-warming the pool removes that injection latency. This affects
    // only the test process, not the library.
    [ModuleInitializer]
    internal static void Init()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(io, 64));
    }
}
