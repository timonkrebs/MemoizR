using MemoizR.StructuredAsyncLock;

namespace MemoizR.Tests;

[Collection("Sequential")]
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
        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        using var _ = await asyncLock.ExclusiveLockAsync();

        // Assert
        var lockScope = asyncLock.LockScope;
        Assert.Equal(1, asyncLock.LocksHeld);
        Assert.Equal(0, asyncLock.UpgradedLocksHeld);
        Assert.NotEqual(0, lockScope);

        using (var disposable = await asyncLock.UpgradeableLockAsync(cancellationTokenSource.Token))
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
        var oldLockScope = asyncLock.LockScope;
        var lockScope = 0;

        // Act
        using (var exclusive = await asyncLock.ExclusiveLockAsync())
        {
            var cancellationTokenSource = new CancellationTokenSource();

            // Assert
            await Task.Delay(5);
            lockScope = asyncLock.LockScope;
            Assert.NotEqual(0, lockScope);
            Assert.NotEqual(oldLockScope, lockScope);
            Assert.Equal(1, asyncLock.LocksHeld);
            Assert.Equal(0, asyncLock.UpgradedLocksHeld);

            using (var upgradeableDisposable = await asyncLock.UpgradeableLockAsync(cancellationTokenSource.Token))
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
}
