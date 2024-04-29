using MemoizR.StructuredAsyncLock.Nito;

namespace MemoizR.StructuredAsyncLock;

public sealed class AsyncAsymmetricLock
{
    /// <summary>
    /// The queue of TCSs that other tasks are awaiting to acquire the lock as upgradeable.
    /// Upgradeable can only execute one instance at a time in the locked scope, but allow for recursive entering. https://learn.microsoft.com/en-us/dotnet/api/system.threading.lockrecursionpolicy?view=net-7.0
    /// They are blocked by exclusive, and one at the time can be upgraded to allow entering exclusive locks.
    /// </summary>
    readonly IAsyncWaitQueue<IDisposable> upgradeable = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// The queue of TCSs that other tasks are awaiting to acquire the lock as exclusive.
    /// Exclusive can not enter upgradeable locks. If they try InvalidOperationException will be thrown, because otherwise it will lead to deadlocks.
    /// Exclusive can execute with as many other exclusive locks simultaneously.
    /// </summary>
    readonly IAsyncWaitQueue<IDisposable> exclusive = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// Number of exclusive locks held; negative if upgradeable lock are held; 0 if no locks are held.
    /// </summary>
    private volatile int locksHeld;
    private volatile int upgradedLocksHeld;
    private int lockScope;
    private readonly Random rand = new();
    private static readonly AsyncLocal<int> AsyncLocalScope = new();

    internal int LockScope
    {
        get
        {
            lock (this)
            {
                return lockScope;
            }
        }
        set
        {
            lock (this)
            {
                lockScope = value;
            }
        }
    }

    internal int LocksHeld
    {
        get
        {
            lock (this)
            {
                return locksHeld;
            }
        }
    }

    internal int UpgradedLocksHeld
    {
        get
        {
            lock (this)
            {
                return upgradedLocksHeld;
            }
        }
    }

    /// <summary>
    /// Applies a continuation to the task that will call <see cref="ReleaseWaiters"/> if the task is canceled. This method may not be called while holding the sync lock.
    /// </summary>
    /// <param name="task">The task to observe for cancellation.</param>
    private void ReleaseWaitersWhenCanceled(Task task)
    {
        task.ContinueWith(_ =>
        {
            lock (this)
            {
                ReleaseWaiters();
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnCanceled | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Asynchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestExclusiveLockAsync(CancellationToken cancellationToken, int lockScope)
    {
        if (LockScope == lockScope && LocksHeld < 0)
        {
            throw new InvalidOperationException("Can not aquire recursive exclusive lock in the scope of an upgradeable lock");
        }

        if (LockScope == lockScope && LocksHeld > 0)
        {
            throw new InvalidOperationException("Can not aquire recursive exclusive locks in the same scope");
        }

        // If the lock is available and there are no waiting upgradeable, or upgrading upgradeable, take it immediately.
        if (LocksHeld == 0 && upgradeable.IsEmpty && UpgradedLocksHeld == 0)
        {
            lock (this)
            {
                Interlocked.Increment(ref locksHeld);
            }
            LockScope = lockScope;
            return Task.FromResult<IDisposable>(new ExclusivePrioKey(this));
        }
        else if (LockScope != lockScope)
        {
            // Wait for the lock to become available or cancellation.
            return exclusive.Enqueue(this, cancellationToken, lockScope);
        }
        else
        {
            throw new InvalidOperationException("Should never happen!");
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> ExclusiveLockAsync(CancellationToken cancellationToken)
    {
        int lockScope;
        lock (this)
        {
            lockScope = AsyncLocalScope.Value;
            if (lockScope == 0)
            {
                lockScope = rand.Next(1, int.MaxValue);
                AsyncLocalScope.Value = lockScope;
            }
            return new AwaitableDisposable<IDisposable>(RequestExclusiveLockAsync(cancellationToken, lockScope));
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> ExclusiveLockAsync()
    {
        return ExclusiveLockAsync(CancellationToken.None);
    }

    /// <summary>
    /// Asynchronously acquires the lock as a upgradeable. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestUpgradeableLockAsync(CancellationToken cancellationToken, int lockScope)
    {
        var canAcquireLock = false;

        if (LocksHeld == 0 && UpgradedLocksHeld == 0)
        {
            lock (this)
            {
                Interlocked.Increment(ref upgradedLocksHeld);
            }
            LockScope = lockScope;
            canAcquireLock = true;
        }
        else if (LockScope == lockScope)
        {
            lock (this)
            {
                Interlocked.Increment(ref upgradedLocksHeld);
            }
            canAcquireLock = true;
        }

        var ret = canAcquireLock
            ? Task.FromResult<IDisposable>(new UpgradeableKey(this))
            : upgradeable.Enqueue(this, cancellationToken, lockScope);
        ReleaseWaitersWhenCanceled(ret);
        return ret;
    }

    /// <summary>
    /// Asynchronously acquires the lock as a Upgradeable. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> UpgradeableLockAsync(CancellationToken cancellationToken)
    {
        int lockScope;
        lock (this)
        {
            lockScope = AsyncLocalScope.Value;
            if (lockScope == 0)
            {
                lockScope = rand.Next(1, int.MaxValue);
                AsyncLocalScope.Value = lockScope;
            }
            return new AwaitableDisposable<IDisposable>(RequestUpgradeableLockAsync(cancellationToken, lockScope));
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a upgradeable. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> UpgradeableLockAsync()
    {
        return UpgradeableLockAsync(CancellationToken.None);
    }

    /// <summary>
    /// Grants lock(s) to waiting tasks. This method assumes the sync lock is already held.
    /// </summary>
    private void ReleaseWaiters()
    {
        if (!upgradeable.IsEmpty && LocksHeld == 0 && UpgradedLocksHeld == 0)
        {
            lock (this)
            {
                Interlocked.Increment(ref upgradedLocksHeld);
            }
            LockScope = upgradeable.Dequeue(new UpgradeableKey(this));
            AsyncLocalScope.Value = LockScope;
        }
        else if (!exclusive.IsEmpty && LocksHeld == 0 && UpgradedLocksHeld == 0)
        {
            lock (this)
            {
                Interlocked.Increment(ref locksHeld);
            }
            LockScope = exclusive.Dequeue(new ExclusivePrioKey(this));
            AsyncLocalScope.Value = LockScope;
        }
        else if ((!upgradeable.IsEmpty || !exclusive.IsEmpty) && LocksHeld == 0 && UpgradedLocksHeld == 0)
        {
            throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// Releases the lock as a exclusive.
    /// </summary>
    internal void ReleaseExclusiveLock()
    {
        lock (this)
        {
            lock (this)
            {
                Interlocked.Decrement(ref locksHeld);
            }
            if (LocksHeld == 0)
            {
                LockScope = 0;
            }
            ReleaseWaiters();
        }
    }

    /// <summary>
    /// Releases the lock as a upgradeable.
    /// </summary>
    internal void ReleaseUpgradeableLock()
    {
        lock (this)
        {
            if (UpgradedLocksHeld > 0)
            {
                lock (this)
                {
                    Interlocked.Decrement(ref upgradedLocksHeld);
                }
                if (UpgradedLocksHeld == 0 && LocksHeld == 0)
                {
                    LockScope = 0;
                }
            }
            else
            {
                throw new InvalidOperationException("Should never happen!");
            }
            ReleaseWaiters();
        }
    }

    /// <summary>
    /// The disposable which releases the exclusive lock.
    /// </summary>
    private sealed class ExclusivePrioKey : IDisposable
    {
        private readonly AsyncAsymmetricLock asyncPriorityLock;

        /// <summary>
        /// Creates the key for a lock.
        /// </summary>
        /// <param name="asyncPriorityLock">The lock to release. May not be <c>null</c>.</param>
        public ExclusivePrioKey(AsyncAsymmetricLock asyncPriorityLock)
        {
            this.asyncPriorityLock = asyncPriorityLock;
        }

        public void Dispose()
        {
            asyncPriorityLock.ReleaseExclusiveLock();
        }
    }

    /// <summary>
    /// The disposable which releases the upgradeable lock.
    /// </summary>
    private sealed class UpgradeableKey : IDisposable
    {
        private readonly AsyncAsymmetricLock asyncPriorityLock;

        /// <summary>
        /// Creates the key for a lock.
        /// </summary>
        /// <param name="asyncPriorityLock">The lock to release. May not be <c>null</c>.</param>
        public UpgradeableKey(AsyncAsymmetricLock asyncPriorityLock)
        {
            this.asyncPriorityLock = asyncPriorityLock;
        }

        public void Dispose()
        {
            asyncPriorityLock.ReleaseUpgradeableLock();
        }
    }
}
