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
}
