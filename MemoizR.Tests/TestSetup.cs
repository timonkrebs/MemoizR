using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MemoizR.Tests;

internal static class TestSetup
{
    // The structured-concurrency and reactive tests start many short-lived async operations
    // and assert against tight [Fact(Timeout=...)] budgets. With the default minimum worker
    // count (= CPU count) the thread pool injects new threads only ~1-2 per second, so
    // Task.Delay continuations can stall ~1s under load and the timing-sensitive tests flake
    // -- especially on CI runners with few cores. Pre-warming the pool removes that injection
    // latency. This affects only the test process, not the library.
    [ModuleInitializer]
    internal static void Init()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(io, 64));
    }
}
