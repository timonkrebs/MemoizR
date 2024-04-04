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
        var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, asyncLock.lockScope);

        // Act
        using (var disposable = await asyncLock.ExclusiveLockAsync())
        {
            var lockScope = asyncLock.lockScope;
            // Assert
            Assert.NotNull(disposable);
            Assert.Equal(1, asyncLock.locksHeld);
            Assert.Equal(0, asyncLock.upgradedLocksHeld);
            Assert.NotEqual(0, lockScope);

            await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            {
                await asyncLock.ExclusiveLockAsync(cancellationTokenSource.Token);
            });
        }
    }

    [Fact(Timeout = 500)]
    public async Task UpgradeableLock_AcquiredAndReleased()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();
        Assert.Equal(0, asyncLock.lockScope);

        // Act
        using (var disposable = await asyncLock.UpgradeableLockAsync())
        {
            var lockScope = asyncLock.lockScope;
            // Assert
            Assert.NotNull(disposable);
            Assert.Equal(-1, asyncLock.locksHeld);
            Assert.Equal(0, asyncLock.upgradedLocksHeld);
            Assert.NotEqual(0, lockScope);

            using (var disposable2 = await asyncLock.UpgradeableLockAsync())
            {
                Assert.NotNull(disposable2);
                Assert.Equal(-2, asyncLock.locksHeld);
                Assert.Equal(0, asyncLock.upgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.lockScope);
            }

            Assert.Equal(-1, asyncLock.locksHeld);
            Assert.Equal(0, asyncLock.upgradedLocksHeld);
            Assert.Equal(lockScope, asyncLock.lockScope);
        }

        Assert.Equal(0, asyncLock.locksHeld);
        Assert.Equal(0, asyncLock.upgradedLocksHeld);
        Assert.Equal(0, asyncLock.lockScope);
    }

    [Fact(Timeout = 500)]
    public async Task ExclusiveLock_BlockedByUpgradeable()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();
        var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        using var _ = await asyncLock.UpgradeableLockAsync();

        // Assert
        Assert.Equal(-1, asyncLock.locksHeld);
        Assert.Equal(0, asyncLock.upgradedLocksHeld);
        Assert.NotEqual(0, asyncLock.lockScope);

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await asyncLock.ExclusiveLockAsync(cancellationTokenSource.Token);
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
        var lockScope = asyncLock.lockScope;
        Assert.Equal(1, asyncLock.locksHeld);
        Assert.Equal(0, asyncLock.upgradedLocksHeld);
        Assert.NotEqual(0, lockScope);

        using (var disposable = await asyncLock.UpgradeableLockAsync(cancellationTokenSource.Token))
        {
            // Assert
            Assert.NotNull(disposable);
            Assert.Equal(1, asyncLock.locksHeld);
            Assert.Equal(1, asyncLock.upgradedLocksHeld);
            Assert.Equal(lockScope, asyncLock.lockScope);

            using (var disposable2 = await asyncLock.UpgradeableLockAsync())
            {
                Assert.NotNull(disposable2);
                Assert.Equal(1, asyncLock.locksHeld);
                Assert.Equal(2, asyncLock.upgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.lockScope);
            }
        }

        Assert.Equal(1, asyncLock.locksHeld);
        Assert.Equal(0, asyncLock.upgradedLocksHeld);
        Assert.NotEqual(0, lockScope);
    }

    [Fact(Timeout = 500)]
    public async Task UpgradeableLock_ThreadSavety()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();

        Assert.Equal(0, asyncLock.lockScope);

        var task1 = Task.Run(async () => await SimulateReaction(asyncLock));
        await Task.Delay(10);
        var task2 = Task.Run(async () => await SimulateReaction(asyncLock));
        await Task.Delay(10);
        var task3 = Task.Run(async () => await SimulateReaction(asyncLock));

        await Task.WhenAll(task1, task2, task3);
        Assert.Equal(0, asyncLock.locksHeld);
        Assert.Equal(0, asyncLock.upgradedLocksHeld);
        Assert.Equal(0, asyncLock.lockScope);
    }

    private async Task SimulateReaction(AsyncAsymmetricLock asyncLock)
    {
        var oldLockScope = asyncLock.lockScope;
        var lockScope = 0;

        // Act
        using (var exclusive = await asyncLock.ExclusiveLockAsync())
        {
            var cancellationTokenSource = new CancellationTokenSource();

            // Assert
            await Task.Delay(100);
            lockScope = asyncLock.lockScope;
            Assert.NotEqual(0, lockScope);
            Assert.NotEqual(oldLockScope, lockScope);
            Assert.Equal(1, asyncLock.locksHeld);
            Assert.Equal(0, asyncLock.upgradedLocksHeld);

            using (var upgradeableDisposable = await asyncLock.UpgradeableLockAsync(cancellationTokenSource.Token))
            {
                // Assert
                Assert.NotNull(upgradeableDisposable);
                Assert.Equal(1, asyncLock.locksHeld);
                Assert.Equal(1, asyncLock.upgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.lockScope);

                using (var upgradeableDisposable2 = await asyncLock.UpgradeableLockAsync())
                {
                    Assert.NotNull(upgradeableDisposable2);
                    Assert.Equal(1, asyncLock.locksHeld);
                    Assert.Equal(2, asyncLock.upgradedLocksHeld);
                    Assert.Equal(lockScope, asyncLock.lockScope);
                }

                Assert.NotNull(upgradeableDisposable);
                Assert.Equal(1, asyncLock.locksHeld);
                Assert.Equal(1, asyncLock.upgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.lockScope);
            }

            Assert.Equal(1, asyncLock.locksHeld);
            Assert.Equal(0, asyncLock.upgradedLocksHeld);
            Assert.Equal(lockScope, asyncLock.lockScope);
        }

        Assert.Equal(0, asyncLock.upgradedLocksHeld);
        Assert.NotEqual(lockScope, asyncLock.lockScope);
        Assert.NotEqual(oldLockScope, asyncLock.lockScope);
    }
}
