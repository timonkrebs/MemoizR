using MemoizR.StructuredAsyncLock;

namespace MemoizR.Tests;

[Collection("Sequential")]
public class AsyncAsymmetricLockTests
{
    [Fact]
    public async Task ExclusiveLock_AcquiredAndReleased()
    {
        // Arrange
        var asyncLock = new AsyncAsymmetricLock();
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

            using (var disposable2 = await asyncLock.ExclusiveLockAsync())
            {
                Assert.NotNull(disposable2);
                Assert.Equal(2, asyncLock.locksHeld);
                Assert.Equal(0, asyncLock.upgradedLocksHeld);
                Assert.Equal(lockScope, asyncLock.lockScope);
            }

            Assert.Equal(1, asyncLock.locksHeld);
            Assert.Equal(0, asyncLock.upgradedLocksHeld);
            Assert.Equal(lockScope, asyncLock.lockScope);
        }

        Assert.Equal(0, asyncLock.locksHeld);
        Assert.Equal(0, asyncLock.upgradedLocksHeld);
        Assert.Equal(0, asyncLock.lockScope);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task UpgradeableLock_BlockedByExclusive()
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

    // Add more test cases as needed
}
