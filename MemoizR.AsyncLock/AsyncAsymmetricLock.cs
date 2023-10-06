namespace MemoizR.AsyncLock;

public class AsyncAsymmetricLock
{
    /// <summary>
    /// The queue of TCSs that other tasks are awaiting to acquire the lock as upgradeable.
    /// Upgradeable can only execute one instance at a time in the locked scope, but allow for recursive entering. https://learn.microsoft.com/en-us/dotnet/api/system.threading.lockrecursionpolicy?view=net-7.0
    /// They are blocked by exclusive, and one at the time can be upgraded to allow entering exclusive locks.
    /// </summary>
    IAsyncWaitQueue<IDisposable> upgradeable = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// The queue of TCSs that other tasks are awaiting to acquire the lock as exclusive.
    /// Exclusive can not enter upgradeable locks. If they try InvalidOperationException will be thrown, because otherwise it will lead to deadlocks.
    /// Exclusive can execute with as many other exclusive locks simultaneously.
    /// </summary>
    IAsyncWaitQueue<IDisposable> exclusive = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// Number of exclusive locks held; negative if upgradeable lock are held; 0 if no locks are held.
    /// </summary>
    private int locksHeld;
    private int upgradedLocksHeld;
    private int lockScope;
    private Random rand = new();
    private static AsyncLocal<int> asyncLocalScope = new AsyncLocal<int>();

    /// <summary>
    /// Applies a continuation to the task that will call <see cref="ReleaseWaiters"/> if the task is canceled. This method may not be called while holding the sync lock.
    /// </summary>
    /// <param name="task">The task to observe for cancellation.</param>
    private void ReleaseWaitersWhenCanceled(Task task)
    {
        task.ContinueWith(t => { lock (this) { ReleaseWaiters(); } }, CancellationToken.None, TaskContinuationOptions.OnlyOnCanceled | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Asynchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestExclusiveLockAsync(CancellationToken cancellationToken)
    {
        lock (this)
        {
            // If the lock is available or in exclusive mode and there are no waiting upgradeable, or upgrading upgradeable, take it immediately.
            if (locksHeld >= 0 && upgradeable.IsEmpty && upgradedLocksHeld == 0)
            {
                ++locksHeld;
                return Task.FromResult<IDisposable>(new ExclusivePrioKey(this));
            }
            else
            {
                // Wait for the lock to become available or cancellation.
                return exclusive.Enqueue(this, cancellationToken, 0);
            }
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> ExclusiveLockAsync(CancellationToken cancellationToken)
    {
        return new AwaitableDisposable<IDisposable>(RequestExclusiveLockAsync(cancellationToken));
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
    /// Synchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable ExclusiveLock(CancellationToken cancellationToken)
    {
        return RequestExclusiveLockAsync(cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Synchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable ExclusiveLock()
    {
        return ExclusiveLock(CancellationToken.None);
    }

    /// <summary>
    /// Asynchronously acquires the lock as a upgradeable. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestUpgradeableLockAsync(CancellationToken cancellationToken, int lockScope)
    {
        Task<IDisposable> ret;
        lock (this)
        {
            // If the lock is available, take it immediately.
            if (locksHeld == 0)
            {
                locksHeld = -1;
#pragma warning disable CA2000 // Dispose objects before losing scope
                ret = Task.FromResult<IDisposable>(new UpgradeableKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
                this.lockScope = lockScope;
            }
            else if (locksHeld > 0 && (this.lockScope == lockScope || this.lockScope == 0))
            {
                if (upgradedLocksHeld == 0)
                {
                    this.lockScope = lockScope;
                }
                this.upgradedLocksHeld++;
#pragma warning disable CA2000 // Dispose objects before losing scope
                ret = Task.FromResult<IDisposable>(new UpgradeableKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
            }
            else if (locksHeld < 0 && this.lockScope == lockScope)
            {
                --locksHeld;
#pragma warning disable CA2000 // Dispose objects before losing scope
                ret = Task.FromResult<IDisposable>(new UpgradeableKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
            }
            else
            {
                // Wait for the lock to become available or cancellation.
                ret = upgradeable.Enqueue(this, cancellationToken, lockScope);
            }
        }

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
            lockScope = asyncLocalScope.Value;
            if (lockScope == 0)
            {
                lockScope = rand.Next();
                asyncLocalScope.Value = lockScope;
            }
        }

        return new AwaitableDisposable<IDisposable>(RequestUpgradeableLockAsync(cancellationToken, lockScope));
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
    /// Synchronously acquires the lock as a upgradeable. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable UpgradeableLock(CancellationToken cancellationToken)
    {
        return RequestUpgradeableLockAsync(cancellationToken, Thread.CurrentThread.ManagedThreadId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously acquires the lock as a upgradeable. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable UpgradeableLock()
    {
        return UpgradeableLock(CancellationToken.None);
    }

    /// <summary>
    /// Grants lock(s) to waiting tasks. This method assumes the sync lock is already held.
    /// </summary>
    private void ReleaseWaiters()
    {
        if (locksHeld < 0) return;

        if (!exclusive.IsEmpty && locksHeld >= 0 && upgradedLocksHeld == 0)
        {
            while (!exclusive.IsEmpty)
            {
#pragma warning disable CA2000 // Dispose objects before losing scope
                lockScope = exclusive.Dequeue(new ExclusivePrioKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
                asyncLocalScope.Value = lockScope;
                ++locksHeld;
            }
        }
        else if (!upgradeable.IsEmpty)
        {
            --locksHeld;
#pragma warning disable CA2000 // Dispose objects before losing scope
            lockScope = upgradeable.Dequeue(new UpgradeableKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
            asyncLocalScope.Value = lockScope;
            return;
        }
    }

    /// <summary>
    /// Releases the lock as a exclusive.
    /// </summary>
    internal void ReleaseExclusiveLock()
    {
        lock (this)
        {
            --locksHeld;
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
            if (upgradedLocksHeld > 0)
            {
                upgradedLocksHeld--;
                if (upgradedLocksHeld == 0)
                {
                    lockScope = 0;
                }
            }
            else
            {
                locksHeld++;
                if (locksHeld == 0)
                {
                    lockScope = 0;
                }
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
