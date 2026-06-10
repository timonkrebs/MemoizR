using MemoizR.StructuredAsyncLock;

namespace MemoizR.Tests;

public class AsyncAsymmetricLockTests
{
    [Fact(Timeout = 500)]
    public async Task ExclusiveLock_AcquiredAndReleased()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();
        var cancellationTokenSource = new CancellationTokenSource();
        Assert.Equal(0, asyncLock.LockScope);

        // Act
        using (var disposable = await asyncLock.ExclusiveLockAsync())
        {
            var lockScope = asyncLock.LockScope;
            // Assert
            Assert.NotNull(disposable);
            Assert.Equal(1, asyncLock.LocksHeld);
            Assert.Equal(0, asyncLock.UpgradedLocksHeld);
            Assert.NotEqual(0, lockScope);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await asyncLock.ExclusiveLockAsync();
            });
        }
    }

    [Fact(Timeout = 500)]
    public async Task UpgradeableLock_AcquiredAndReleased()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();
        Assert.Equal(0, asyncLock.LockScope);

        // Act
        using (var disposable = await asyncLock.UpgradeableLockAsync())
        {
            var lockScope = asyncLock.LockScope;
            // Assert
            Assert.NotNull(disposable);
            Assert.Equal(0, asyncLock.LocksHeld);
            Assert.Equal(1, asyncLock.UpgradedLocksHeld);
            Assert.NotEqual(0, lockScope);

            using (var disposable2 = await asyncLock.UpgradeableLockAsync())
            {
                Assert.NotNull(disposable2);
                Assert.Equal(0, asyncLock.LocksHeld);
                Assert.Equal(2, asyncLock.UpgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.LockScope);
            }

            Assert.Equal(0, asyncLock.LocksHeld);
            Assert.Equal(1, asyncLock.UpgradedLocksHeld);
            Assert.Equal(lockScope, asyncLock.LockScope);
        }

        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }

    [Fact(Timeout = 500)]
    public async Task ExclusiveLock_BlockedByUpgradeable()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();

        // Act
        using var _ = await asyncLock.UpgradeableLockAsync();

        // Assert
        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(1, asyncLock.UpgradedLocksHeld);
        Assert.NotEqual(0, asyncLock.LockScope);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await asyncLock.ExclusiveLockAsync();
        });
    }

    [Fact(Timeout = 500)]
    public async Task UpgradeableLock_NotBlockedByExclusive()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();

        // Act
        using var _ = await asyncLock.ExclusiveLockAsync();

        // Assert
        var lockScope = asyncLock.LockScope;
        Assert.Equal(1, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.NotEqual(0, lockScope);

        using (var disposable = await asyncLock.UpgradeableLockAsync())
        {
            Assert.NotNull(disposable);
            Assert.Equal(1, asyncLock.LocksHeld);
            Assert.Equal(1, asyncLock.UpgradedLocksHeld);
            Assert.Equal(lockScope, asyncLock.LockScope);

            using (var disposable2 = await asyncLock.UpgradeableLockAsync())
            {
                Assert.NotNull(disposable2);
                Assert.Equal(1, asyncLock.LocksHeld);
                Assert.Equal(2, asyncLock.UpgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.LockScope);
            }
        }

        Assert.Equal(1, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.NotEqual(0, lockScope);
    }

    [Fact(Timeout = 10000)]
    public async Task UpgradeableLock_ThreadSavety()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();

        Assert.Equal(0, asyncLock.LockScope);

        var task1 = SimulateGet(asyncLock);
        await Task.Delay(10);
        var task2 = SimulateReaction(asyncLock);
        await Task.Delay(10);
        var task3 = SimulateGet(asyncLock);
        await Task.Delay(10);
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () => await SimulateReaction(asyncLock)));
            tasks.Add(Task.Run(async () => await SimulateGet(asyncLock)));
            tasks.Add(Task.Run(async () => await SimulateReaction(asyncLock)));
            tasks.Add(Task.Run(async () => await SimulateReaction(asyncLock)));
        }

        await Task.WhenAll([task1, task2, task3, .. tasks]);
        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }

    private async Task SimulateGet(AsyncAsymmetricLock asyncLock)
    {
        // Act
        using (var upgradeableDisposable2 = await asyncLock.UpgradeableLockAsync())
        {
            // Assert
            Assert.NotNull(upgradeableDisposable2);
            Assert.Equal(0, asyncLock.LocksHeld);
            Assert.Equal(1, asyncLock.UpgradedLocksHeld);
            Assert.NotEqual(0, asyncLock.LockScope);
            await Task.Delay(5);
        }
    }

    private async Task SimulateReaction(AsyncAsymmetricLock asyncLock)
    {
        var oldLockScope = asyncLock.LockScope == 0 ? 42 : asyncLock.LockScope;
        double lockScope = 0;

        // Act
        using (var exclusive = await asyncLock.ExclusiveLockAsync())
        {
            // Assert
            await Task.Delay(5);
            lockScope = asyncLock.LockScope;
            Assert.NotEqual(0, lockScope);
            Assert.NotEqual(oldLockScope, lockScope);
            Assert.Equal(1, asyncLock.LocksHeld);
            Assert.Equal(0, asyncLock.UpgradedLocksHeld);

            using (var upgradeableDisposable = await asyncLock.UpgradeableLockAsync())
            {
                Assert.NotNull(upgradeableDisposable);
                Assert.Equal(1, asyncLock.LocksHeld);
                Assert.Equal(1, asyncLock.UpgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.LockScope);

                using (var upgradeableDisposable2 = await asyncLock.UpgradeableLockAsync())
                {
                    Assert.NotNull(upgradeableDisposable2);
                    Assert.Equal(1, asyncLock.LocksHeld);
                    Assert.Equal(2, asyncLock.UpgradedLocksHeld);
                    Assert.Equal(lockScope, asyncLock.LockScope);
                }

                Assert.NotNull(upgradeableDisposable);
                Assert.Equal(1, asyncLock.LocksHeld);
                Assert.Equal(1, asyncLock.UpgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.LockScope);
            }

            Assert.Equal(1, asyncLock.LocksHeld);
            Assert.Equal(0, asyncLock.UpgradedLocksHeld);
            Assert.Equal(lockScope, asyncLock.LockScope);
        }

        // Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.NotEqual(lockScope, asyncLock.LockScope);
        Assert.NotEqual(oldLockScope, asyncLock.LockScope);
    }

    // --- Recursive exclusive locks must fail (and stay failing across refactorings) ---

    [Fact(Timeout = 500)]
    public async Task ExclusiveLock_RecursiveInSameScope_Throws()
    {
        var asyncLock = new AsyncAsymmetricLock();

        using var outer = await asyncLock.ExclusiveLockAsync();

        // A second exclusive acquire in the same async flow (same lock scope) must fail rather
        // than deadlock. If the guard is ever removed this falls through to "Should never
        // happen!", so asserting the message keeps the recursive guard honest.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var inner = await asyncLock.ExclusiveLockAsync();
        });
        Assert.Contains("recursive exclusive", ex.Message);

        // The rejected acquire must not have corrupted the lock state.
        Assert.Equal(1, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
    }

    [Fact(Timeout = 500)]
    public async Task ExclusiveLock_WhileHoldingUpgradeableInSameScope_Throws()
    {
        var asyncLock = new AsyncAsymmetricLock();

        using var upgradeable = await asyncLock.UpgradeableLockAsync();

        // Taking an exclusive lock while already holding an upgradeable one in the same scope
        // would deadlock, so it must throw instead of blocking. Assert the message so the
        // dedicated guard stays honest: before the guard checked UpgradedLocksHeld, this case
        // fell through to the generic "Should never happen!" invariant branch, and a type-only
        // assertion green-lit that accidental path.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var exclusive = await asyncLock.ExclusiveLockAsync();
        });
        Assert.Contains("in the scope of an upgradeable", ex.Message);

        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(1, asyncLock.UpgradedLocksHeld);
    }

    // --- Per-flow reentrancy contract ---

    [Fact(Timeout = 1000)]
    public async Task UpgradeableLock_RecursiveInSameScope_IsGranted()
    {
        var asyncLock = new AsyncAsymmetricLock();

        using (await asyncLock.UpgradeableLockAsync())
        {
            Assert.Equal(1, asyncLock.UpgradedLocksHeld);

            // Same async flow (same lock scope): the upgradeable lock is reentrant -- this is
            // what lets nested Get/UpdateIfNecessary on one flow not self-deadlock.
            using (await asyncLock.UpgradeableLockAsync())
            {
                Assert.Equal(2, asyncLock.UpgradedLocksHeld);
            }

            Assert.Equal(1, asyncLock.UpgradedLocksHeld);
        }

        // Fully drained: nothing leaked, the scope is released.
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }

    // --- Mutual exclusion / no lost wake-ups (fail loudly if a refactoring breaks the lock) ---

    [Fact(Timeout = 10000)]
    public async Task ExclusiveLock_ManyConcurrent_SerializeWithoutLostUpdatesOrLeaks()
    {
        var asyncLock = new AsyncAsymmetricLock();
        var counter = 0;

        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            using (await asyncLock.ExclusiveLockAsync())
            {
                // Non-atomic read-modify-write across an await: only correct if the lock truly
                // serializes every holder. A broken lock loses updates here.
                var snapshot = counter;
                await Task.Yield();
                counter = snapshot + 1;
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(100, counter);                  // mutual exclusion held => no lost updates
        Assert.Equal(0, asyncLock.LocksHeld);        // every acquire released => no leak
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);        // fully drained => no lost wake-up / deadlock
    }

    [Fact(Timeout = 10000)]
    public async Task UpgradeableLock_ManyConcurrent_SerializeWithoutLostUpdatesOrLeaks()
    {
        var asyncLock = new AsyncAsymmetricLock();
        var counter = 0;

        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            using (await asyncLock.UpgradeableLockAsync())
            {
                var snapshot = counter;
                await Task.Yield();
                counter = snapshot + 1;
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(100, counter);
        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }

    // Mixed-mode stress: exclusive and upgradeable acquirers from independent flows interleave
    // arbitrarily. Cross-scope they must all mutually exclude, and the wake-up chain through
    // ReleaseWaiters must drain both queues -- a priority/handoff bug between the two waiter
    // queues only surfaces in mixed mode, which the single-mode stress tests never enter.
    [Fact(Timeout = 10000)]
    public async Task MixedLocks_ManyConcurrent_SerializeWithoutLostUpdatesOrLeaks()
    {
        var asyncLock = new AsyncAsymmetricLock();
        var counter = 0;

        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            using (await (i % 2 == 0 ? asyncLock.ExclusiveLockAsync() : asyncLock.UpgradeableLockAsync()))
            {
                var snapshot = counter;
                await Task.Yield();
                counter = snapshot + 1;
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(100, counter);                  // full mutual exclusion across both modes
        Assert.Equal(0, asyncLock.LocksHeld);        // both queues fully drained
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }

    // --- Regression: releasing a lock must not corrupt the releasing flow's own scope ---

    [Fact(Timeout = 5000)]
    public async Task ReleasingFlow_DoesNotCorruptScope_WhenHandingOffToWaiter()
    {
        var asyncLock = new AsyncAsymmetricLock();
        // RunContinuationsAsynchronously: SetResult must not run the other flow's continuation
        // inline on the setter's stack, where it could contend the very lock the setter holds.
        var holderAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reacquireOutcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Holder runs in its own flow (its own scope).
        var holderTask = Task.Run(async () =>
        {
            var held = await asyncLock.ExclusiveLockAsync();
            holderAcquired.SetResult();
            await waiterEnqueued.Task;     // a different flow is now queued behind us
            held.Dispose();                // hand the lock off to the waiter
            await waiterAcquired.Task;     // waiter holds it now

            // Re-acquire in THIS flow. It must just wait for the waiter, never throw. With the
            // old scope-corruption bug, releasing stamped the waiter's scope onto this flow, so
            // this acquire saw "its own" scope as held and threw "recursive exclusive ...".
            try
            {
                var reacquire = asyncLock.ExclusiveLockAsync();   // should enqueue behind the waiter
                releaseWaiter.SetResult();                         // let the waiter finish
                using (await reacquire) { }                        // granted once the waiter releases
                reacquireOutcome.SetResult("ok");
            }
            catch (Exception e)
            {
                releaseWaiter.TrySetResult();
                reacquireOutcome.SetResult($"threw {e.GetType().Name}: {e.Message}");
            }
        });

        await holderAcquired.Task;

        // Waiter runs in a SEPARATE root flow, so it gets a distinct lock scope.
        var waiterTask = Task.Run(async () =>
        {
            var acquire = asyncLock.ExclusiveLockAsync();   // enqueues behind the holder
            waiterEnqueued.SetResult();
            using (await acquire)
            {
                waiterAcquired.SetResult();
                await releaseWaiter.Task;
            }
        });

        Assert.Equal("ok", await reacquireOutcome.Task);
        await Task.WhenAll(holderTask, waiterTask);
        Assert.Equal(0, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.Equal(0, asyncLock.LockScope);
    }
}
