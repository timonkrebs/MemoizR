using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace MemoizR.Tests;

public class CoyoteTests
{
    private readonly ITestOutputHelper Output;

    public CoyoteTests(ITestOutputHelper output)
    {
        this.Output = output;
    }

    [Fact]
    public void TestThreadSafety3Systematic()
    {
        var config = Configuration.Create().WithTestingIterations(100);
        TestingEngine engine = TestingEngine.Create(config, CoyoteTestThreadSafety3);
        engine.Run();
        Output.WriteLine("Coyote found {0} bug(s).", engine.TestReport.NumOfFoundBugs);
        Assert.Equal(0, engine.TestReport.NumOfFoundBugs);
    }

    private async Task CoyoteTestThreadSafety3()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(4);
        var m1 = f.CreateMemoizR(async () => await v1.Get() * 2);

        var result1 = 0;
        var r1 = f.BuildReaction().CreateReaction(m1, m => result1 = m);
        var result2 = 0;
        var r2 = f.BuildReaction().CreateReaction(m1, m => result2 = m);

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++) // Reduced iterations for systematic testing
        {
            var j = i;
            tasks.Add(Task.Run(async () => await v1.Set(j)));
        }
        await Task.WhenAll(tasks);
        await v1.Set(41);

        var resultM1 = await m1.Get();

        // Use Specification.Assert for Coyote systematic testing
        Specification.Assert(resultM1 == 82, $"Expected 82, got {resultM1}");
        Specification.Assert(result1 == 82, $"Expected result1 to be 82, got {result1}");
        Specification.Assert(result2 == 82, $"Expected result2 to be 82, got {result2}");
    }

    [Fact]
    public void TestThreadSafety5Systematic()
    {
        var config = Configuration.Create().WithTestingIterations(100);
        TestingEngine engine = TestingEngine.Create(config, CoyoteTestThreadSafety5);
        engine.Run();
        Output.WriteLine("Coyote found {0} bug(s).", engine.TestReport.NumOfFoundBugs);
        Assert.Equal(0, engine.TestReport.NumOfFoundBugs);
    }

    private async Task CoyoteTestThreadSafety5()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(4);

        var memoInvocationCount = 0;
        var m1 = f.CreateMemoizR("m1", async () =>
        {
            Interlocked.Increment(ref memoInvocationCount);
            return await v1.Get() * 2;
        });

        var m2 = f.CreateMemoizR("m2", async () =>
        {
            return await m1.Get() * 2;
        });

        var invocationCountR1 = 0;
        var r1 = f.BuildReaction().CreateReaction(m2, m => Interlocked.Increment(ref invocationCountR1));
        var invocationCountR2 = 0;
        var r2 = f.BuildReaction().CreateReaction(m2, m => Interlocked.Increment(ref invocationCountR2));

        var tasks = Enumerable.Range(1, 100).Select(i => Task.Run(async () => await v1.Set(i))).ToList();
        await Task.WhenAll(tasks);

        await v1.Set(41);

        var resultM1 = await m1.Get();
        Specification.Assert(resultM1 == 82, $"Expected 82, got {resultM1}");

        Specification.Assert(memoInvocationCount >= 1, "Must be invoked at least once");
        Specification.Assert(invocationCountR1 <= memoInvocationCount, "R1 invocation count should be <= memo invocation count");
        Specification.Assert(invocationCountR2 <= memoInvocationCount, "R2 invocation count should be <= memo invocation count");
    }
}
