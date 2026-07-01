using MemoizR.StructuredAsyncLock;

namespace MemoizR.Tests;

// Contracts of the dynamic isolation checks (issue #36, the runtime analog of Swift's
// preconditionIsolated / SE-0423): AsyncAsymmetricLock.IsHeldByCurrentFlow follows the lock's
// flow-scoped reentrancy semantics, and Context/MemoFactory.AssertEvaluationIsolated
// distinguishes code inside a serialized graph evaluation from code outside one. The DEBUG-only
// rewiring assert in SignalHandlR is exercised implicitly by every recompute in the suite.
public class IsolationAssertionTests
{
    // The error message lists Set among the isolated graph evaluations, and
    // EagerRelativeSignal.Set runs USER code under the exclusive lock -- so that callback must
    // read as isolated. It only can if Set pins the scope whose lock it holds (a throwaway
    // scope would make the callback resolve a different instance and read as not isolated).
    [Fact(Timeout = 10000)]
    public async Task AssertEvaluationIsolated_Passes_InsideATopLevelSetCallback()
    {
        var f = new MemoFactory();
        var relative = f.CreateEagerRelativeSignal(1);

        var observedIsolated = false;
        await relative.Set(v =>
        {
            f.AssertEvaluationIsolated(); // throws if the held exclusive lock is not recognized
            observedIsolated = true;
            return v + 1;
        });

        Assert.True(observedIsolated);
        Assert.Equal(2, await relative.Get());
    }

    [Fact(Timeout = 10000)]
    public async Task IsHeldByCurrentFlow_TracksAcquireAndRelease_InBothModes()
    {
        var asyncLock = new AsyncAsymmetricLock();
        Assert.False(asyncLock.IsHeldByCurrentFlow);

        using (await asyncLock.UpgradeableLockAsync())
        {
            Assert.True(asyncLock.IsHeldByCurrentFlow);
        }

        Assert.False(asyncLock.IsHeldByCurrentFlow);

        using (await asyncLock.ExclusiveLockAsync())
        {
            Assert.True(asyncLock.IsHeldByCurrentFlow);
        }

        Assert.False(asyncLock.IsHeldByCurrentFlow);
    }

    [Fact(Timeout = 10000)]
    public async Task IsHeldByCurrentFlow_IsFalseOnAnUnrelatedFlow()
    {
        var asyncLock = new AsyncAsymmetricLock();
        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Spawned BEFORE the acquisition, so this flow does not inherit the holder's scope.
        var observedOnOtherFlow = Task.Run(async () =>
        {
            await holderEntered.Task;
            return asyncLock.IsHeldByCurrentFlow;
        });

        using (await asyncLock.ExclusiveLockAsync())
        {
            holderEntered.SetResult();
            Assert.False(await observedOnOtherFlow);
            Assert.True(asyncLock.IsHeldByCurrentFlow);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task IsHeldByCurrentFlow_CountsChildTasks_AsTheHoldingFlow()
    {
        var asyncLock = new AsyncAsymmetricLock();
        using (await asyncLock.UpgradeableLockAsync())
        {
            // A child task spawned inside the locked region inherits the execution context and
            // would be granted a recursive acquisition (this is how StructuredReduceJob children
            // share their parent's lock), so it counts as the holding flow.
            Assert.True(await Task.Run(() => asyncLock.IsHeldByCurrentFlow));
        }
    }

    [Fact]
    public void AssertEvaluationIsolated_Throws_OutsideAnyEvaluation()
    {
        var f = new MemoFactory();
        Assert.False(f.Context.IsEvaluationIsolated);
        var ex = Assert.Throws<InvalidOperationException>(f.AssertEvaluationIsolated);
        Assert.Contains("no evaluation is active", ex.Message);
    }

    [Fact(Timeout = 10000)]
    public async Task AssertEvaluationIsolated_Passes_InsideAMemoComputation()
    {
        var f = new MemoFactory();
        var v = f.CreateSignal(1);
        var isolatedDuringCompute = false;
        var m = f.CreateMemoizR(async () =>
        {
            f.AssertEvaluationIsolated(); // a failure here faults the Get and the test
            isolatedDuringCompute = f.Context.IsEvaluationIsolated;
            return await v.Get();
        });

        Assert.Equal(1, await m.Get());
        Assert.True(isolatedDuringCompute);
    }
}
