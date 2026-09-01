using System.Diagnostics;
using MemoizR;

// Micro-harness comparing the library's hot paths across builds (see the csproj for how to
// point it at a different checkout). Not BenchmarkDotNet-rigorous, but with warmup and
// best-of-5 medians it is solid for the order-of-magnitude questions it exists to answer:
// what do causality stamps cost on Set/recompute, and do the clean read fast paths stay
// allocation-free?
class Bench
{
    static async Task Main()
    {
        await Measure("signal.Set (distinct values)", 1_000_000, n => SetDistinct(new MemoFactory(), n));
        await Measure("signal.Set (same value)", 1_000_000, n => SetSame(new MemoFactory(), n));
        await Measure("memo.Get (clean, untracked fast path)", 2_000_000, n => CleanGet(new MemoFactory(), n));
        await Measure("chain recompute (Set + Get through 3 memos)", 50_000, n => ChainRecompute(new MemoFactory(), n));
        await Measure("diamond recompute (Set + Get, c = a + b)", 50_000, n => DiamondRecompute(new MemoFactory(), n));

        // The stamps-disabled variants quantify what MemoFactoryOptions.DisableCausalityStamps
        // saves, on the SAME scenarios. Resolved at runtime so this harness still compiles
        // against builds that predate the flag (they simply skip these rows).
        if (Enum.TryParse<MemoFactoryOptions>("DisableCausalityStamps", out var noStamps))
        {
            await Measure("signal.Set (distinct, stamps disabled)", 1_000_000, n => SetDistinct(new MemoFactory(options: noStamps), n));
            await Measure("chain recompute (stamps disabled)", 50_000, n => ChainRecompute(new MemoFactory(options: noStamps), n));
            await Measure("diamond recompute (stamps disabled)", 50_000, n => DiamondRecompute(new MemoFactory(options: noStamps), n));
        }
    }

    static async Task SetDistinct(MemoFactory f, int n)
    {
        var s = f.CreateSignal(0);
        for (var i = 1; i <= n; i++) await s.Set(i);
    }

    static async Task SetSame(MemoFactory f, int n)
    {
        var s = f.CreateSignal(42);
        for (var i = 0; i < n; i++) await s.Set(42);
    }

    static async Task CleanGet(MemoFactory f, int n)
    {
        var s = f.CreateSignal(1);
        var m = f.CreateMemoizR(async () => await s.Get() + 1);
        await m.Get();
        for (var i = 0; i < n; i++) await m.Get();
    }

    static async Task ChainRecompute(MemoFactory f, int n)
    {
        var s = f.CreateSignal(0);
        var m1 = f.CreateMemoizR(async () => await s.Get() + 1);
        var m2 = f.CreateMemoizR(async () => await m1.Get() + 1);
        var m3 = f.CreateMemoizR(async () => await m2.Get() + 1);
        for (var i = 1; i <= n; i++) { await s.Set(i); await m3.Get(); }
    }

    static async Task DiamondRecompute(MemoFactory f, int n)
    {
        var s = f.CreateSignal(0);
        var a = f.CreateMemoizR(async () => await s.Get() + 1);
        var b = f.CreateMemoizR(async () => await s.Get() * 2);
        var c = f.CreateMemoizR(async () => await a.Get() + await b.Get());
        for (var i = 1; i <= n; i++) { await s.Set(i); await c.Get(); }
    }

    static async Task Measure(string name, int n, Func<int, Task> run)
    {
        await run(Math.Max(1000, n / 20)); // warmup + JIT
        var bestNs = double.MaxValue; var bytesPer = 0.0;
        for (var round = 0; round < 5; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            // Process-wide, not per-thread: the scenarios await inside their loops, so
            // continuations hop pool threads and per-thread counters would miss (or even
            // negatively skew) everything allocated off the starting thread. Nothing else
            // allocates concurrently in this single-scenario process, and best-of-5 absorbs
            // the residual noise.
            var a0 = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            await run(n);
            sw.Stop();
            var ns = sw.Elapsed.TotalNanoseconds / n;
            if (ns < bestNs) { bestNs = ns; bytesPer = (GC.GetTotalAllocatedBytes(precise: true) - a0) / (double)n; }
        }
        Console.WriteLine($"{name,-46} {bestNs,8:F0} ns/op {bytesPer,8:F0} B/op");
    }
}
