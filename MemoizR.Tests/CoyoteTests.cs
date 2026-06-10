using Xunit;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoizR;
using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;

namespace MemoizR.Tests;

public class CoyoteTests
{
    [Fact]
    public void TestThreadSafetyWithCoyote()
    {
        // Coyote can only control scheduling when the assemblies under test have been
        // instrumented with `coyote rewrite` (CI does this before running this test).
        // On a plain `dotnet test` the assemblies are not rewritten, so the engine runs only
        // partially-controlled and reports false deadlocks -- skip rather than fail spuriously.
        if (!IsCoyoteRewritten(typeof(MemoFactory).Assembly))
        {
            return;
        }

        var configuration = Configuration.Create()
            .WithTestingIterations(100);
        var engine = TestingEngine.Create(configuration, TestThreadSafety3Iteration);
        engine.Run();
        if (engine.TestReport.NumOfFoundBugs > 0)
        {
            var bug = engine.TestReport.BugReports.First();
            throw new Exception($"Coyote found a bug: {bug}");
        }
    }

    // Rewritten assemblies carry Coyote's RewritingSignatureAttribute. Match by name so we
    // don't take a compile-time dependency on the (internal) attribute type.
    private static bool IsCoyoteRewritten(Assembly assembly) =>
        assembly.GetCustomAttributesData()
            .Any(a => a.AttributeType.FullName == "Microsoft.Coyote.Rewriting.RewritingSignatureAttribute");

    private async Task TestThreadSafety3Iteration()
    {
        var f = new MemoFactory();

        var v1 = f.CreateSignal(4);

        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var j = i;
            tasks.Add(Task.Run(async () => await v1.Set(j)));
        }
        await Task.WhenAll(tasks);
        await v1.Set(41);

        if (await m1.Get() != 82) throw new Exception($"m1.Get() expected 82, but got {await m1.Get()}");
    }

    // Systematic exploration of the cross-flow lost-update class on a two-level chain: Sets on
    // one flow race Gets (which pull recomputes through p into c) on others. This is the shape
    // where a Stale suppressed at an already-dirty node, or a Clean commit racing a concurrent
    // invalidation, can leave a node cached-stale -- p = s/2 keeps p's value identical across
    // adjacent writes so the diamond down-link cannot mask a missed invalidation of c.
    [Fact]
    public void TestChainLostUpdateWithCoyote()
    {
        if (!IsCoyoteRewritten(typeof(MemoFactory).Assembly))
        {
            return;
        }

        // Potential-deadlock reporting is disabled because CacheStateCell's gate is the .NET 9
        // System.Threading.Lock, which this Coyote version cannot rewrite/control; with that
        // uncontrolled lock in play Coyote falls back to its periodic heuristic monitor, which
        // fires spuriously under the heavy interleaving of this chain workload (the bug this
        // test hunts -- a node cached-stale after the race -- is caught by the value assertion
        // in the iteration, and real hangs still fail the suite via the CI job timeout). The
        // deadlock timeout only controls how long a spuriously-"hung" iteration waits before
        // being abandoned; the default 5s made those iterations dominate the runtime.
        var configuration = Configuration.Create()
            .WithTestingIterations(100)
            .WithDeadlockTimeout(100)
            .WithPotentialDeadlocksReportedAsBugs(false);
        var engine = TestingEngine.Create(configuration, TestChainLostUpdateIteration);
        engine.Run();
        if (engine.TestReport.NumOfFoundBugs > 0)
        {
            var bug = engine.TestReport.BugReports.First();
            throw new Exception($"Coyote found a bug: {bug}");
        }
    }

    private async Task TestChainLostUpdateIteration()
    {
        var f = new MemoFactory();
        var s = f.CreateSignal(0);
        var p = f.CreateMemoizR(async () => await s.Get() / 2);
        var c = f.CreateMemoizR(async () => await p.Get());
        await c.Get(); // prime the chain so the links exist before the race starts

        var tasks = new List<Task>();
        for (var i = 1; i <= 3; i++)
        {
            var j = i;
            tasks.Add(Task.Run(async () => await s.Set(j)));
            tasks.Add(Task.Run(async () => await c.Get()));
        }
        await Task.WhenAll(tasks);

        // After the race quiesces, a fresh write must propagate through both levels: a node
        // wrongly committed Clean over a pending invalidation would serve the stale value here.
        await s.Set(40);
        var result = await c.Get();
        if (result != 20) throw new Exception($"c.Get() expected 20, but got {result}");
    }
}
