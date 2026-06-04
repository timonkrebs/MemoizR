using System.Linq;
using xRetry;
using MemoizR.StructuredAsyncLock;
using MemoizR.StructuredConcurrency;

namespace MemoizR.Tests;

// Regression tests covering bugs fixed while stabilising the flaky test suite.
public class RegressionTests
{
    // The lock must hand back a plain Task<IDisposable>. It previously returned the custom
    // AwaitableDisposable awaitable, which Coyote's binary rewriter could not resolve
    // (MissingMethodException broke every lock-using test under `coyote rewrite`).
    // This compiles only while the return type stays assignable to Task<IDisposable>.
    [Fact(Timeout = 1000)]
    public async Task LockMethodsReturnPlainTask()
    {
        var asyncLock = new AsyncAsymmetricLock();

        Task<IDisposable> exclusive = asyncLock.ExclusiveLockAsync();
        (await exclusive).Dispose();

        Task<IDisposable> upgradeable = asyncLock.UpgradeableLockAsync();
        (await upgradeable).Dispose();

        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }

    // Scope keys are generated with a thread-safe RNG (Random.Shared). Previously a static
    // `new Random()` shared across all contexts but guarded only by a per-instance lock could
    // be corrupted by concurrent access and hand out colliding/zero scope keys. Many contexts
    // built and exercised in parallel must each compute correctly.
    [Fact(Timeout = 30000)]
    public async Task ParallelFactoriesDoNotCorruptScopes()
    {
        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var f = new MemoFactory();
            var v = f.CreateSignal(i);
            var m = f.CreateMemoizR(async () => await v.Get() * 2);

            Assert.Equal(i * 2, await m.Get());
            await v.Set(i + 1);
            Assert.Equal((i + 1) * 2, await m.Get());
        }));

        await Task.WhenAll(tasks);
    }

    // A failing structured job must surface an AggregateException that still contains the
    // underlying fault. The catch path previously relied on ConfigureAwaitOptions.SuppressThrowing,
    // which the Coyote rewriter does not honour, collapsing it to a single bare exception.
    [Fact(Timeout = 5000)]
    public async Task FailingJobThrowsAggregateContainingFault()
    {
        var f = new MemoFactory();
        var c1 = f.CreateConcurrentMapReduce(
            (IStructuredResourceGroup c) => Task.FromException<int>(new InvalidOperationException("boom")),
            async c => { await Task.Delay(50, c.Token); return 1; });

        var ex = await Assert.ThrowsAsync<AggregateException>(c1.Get);
        Assert.Contains(ex.Flatten().InnerExceptions, e => e.Message == "boom");
    }

    // Rapid updates inside the debounce window must collapse to a single reaction run with the
    // final value. The previous debounce used Task.Delay(...).ContinueWith, which ran even when
    // the delay was cancelled, so superseded updates were not actually dropped.
    [RetryFact(3, 200)]
    public async Task DebounceCoalescesRapidUpdates()
    {
        var f = new MemoFactory();
        var v1 = f.CreateSignal(0);
        var invocations = 0;
        var last = -1;

        f.BuildReaction()
            .AddDebounceTime(TimeSpan.FromMilliseconds(50))
            .CreateReaction(v1, v => { Interlocked.Increment(ref invocations); last = v; });

        await Task.Delay(200);
        var afterInitial = invocations; // the initial reaction run

        for (var i = 1; i <= 10; i++)
        {
            await v1.Set(i);
        }

        await Task.Delay(300);

        Assert.Equal(10, last);
        Assert.Equal(afterInitial + 1, invocations);
    }
}
