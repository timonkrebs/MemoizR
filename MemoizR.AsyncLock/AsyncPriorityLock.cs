namespace MemoizR.AsyncLock;

public class AsyncPriorityLock
{
    /// <summary>
    /// The queue of TCSs that other tasks are awaiting to acquire the lock as higherPrio.
    /// </summary>
    IAsyncWaitQueue<IDisposable> higherPrio = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// The queue of TCSs that other tasks are awaiting to acquire the lock as lowerPrio.
    /// </summary>
    IAsyncWaitQueue<IDisposable> lowerPrio = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// Number of lowerPrio locks held; negative if higherPrio lock are held; 0 if no locks are held.
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
    /// Asynchronously acquires the lock as a lowerPrio. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestLowerPrioLockAsync(CancellationToken cancellationToken)
    {
        lock (this)
        {
            // If the lock is available or in lowerPrio mode and there are no waiting writers, upgradeable lowerPrio, or upgrading lowerPrio, take it immediately.
            if (locksHeld >= 0 && higherPrio.IsEmpty && upgradedLocksHeld == 0)
            {
                ++locksHeld;
                return Task.FromResult<IDisposable>(new LowerPrioKey(this));
            }
            else
            {
                // Wait for the lock to become available or cancellation.
                return lowerPrio.Enqueue(this, cancellationToken, 0);
            }
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a lowerPrio. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> LowerPrioLockAsync(CancellationToken cancellationToken)
    {
        return new AwaitableDisposable<IDisposable>(RequestLowerPrioLockAsync(cancellationToken));
    }

    /// <summary>
    /// Asynchronously acquires the lock as a lowerPrio. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> LowerPrioLockAsync()
    {
        return LowerPrioLockAsync(CancellationToken.None);
    }

    /// <summary>
    /// Synchronously acquires the lock as a lowerPrio. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable LowerPrioLock(CancellationToken cancellationToken)
    {
        return RequestLowerPrioLockAsync(cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Synchronously acquires the lock as a lowerPrio. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable LowerPrioLock()
    {
        return LowerPrioLock(CancellationToken.None);
    }

    /// <summary>
    /// Asynchronously acquires the lock as a higherPrio. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestHigherPrioLockAsync(CancellationToken cancellationToken, int lockScope)
    {
        Task<IDisposable> ret;
        lock (this)
        {
            // If the lock is available, take it immediately.
            if (locksHeld == 0)
            {
                locksHeld = -1;
#pragma warning disable CA2000 // Dispose objects before losing scope
                ret = Task.FromResult<IDisposable>(new HigherPrioKey(this));
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
                ret = Task.FromResult<IDisposable>(new HigherPrioKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
            }
            else if (locksHeld < 0 && this.lockScope == lockScope)
            {
                --locksHeld;
#pragma warning disable CA2000 // Dispose objects before losing scope
                ret = Task.FromResult<IDisposable>(new HigherPrioKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
            }
            else
            {
                // Wait for the lock to become available or cancellation.
                ret = higherPrio.Enqueue(this, cancellationToken, lockScope);
            }
        }

        ReleaseWaitersWhenCanceled(ret);
        return ret;
    }

    /// <summary>
    /// Asynchronously acquires the lock as a HigherPrio. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> HigherPrioLockAsync(CancellationToken cancellationToken)
    {
        var lockScope = asyncLocalScope.Value;
        if (lockScope == 0)
        {
            lockScope = rand.Next();
            asyncLocalScope.Value = lockScope;
        }
        return new AwaitableDisposable<IDisposable>(RequestHigherPrioLockAsync(cancellationToken, lockScope));
    }

    /// <summary>
    /// Asynchronously acquires the lock as a higherPrio. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> HigherPrioLockAsync()
    {
        return HigherPrioLockAsync(CancellationToken.None);
    }

    /// <summary>
    /// Synchronously acquires the lock as a higherPrio. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable HigherPrioLock(CancellationToken cancellationToken)
    {
        return RequestHigherPrioLockAsync(cancellationToken, Thread.CurrentThread.ManagedThreadId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously acquires the lock as a higherPrio. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable HigherPrioLock()
    {
        return HigherPrioLock(CancellationToken.None);
    }

    /// <summary>
    /// Grants lock(s) to waiting tasks. This method assumes the sync lock is already held.
    /// </summary>
    private void ReleaseWaiters()
    {
        if (locksHeld < 0) return;

        if (!lowerPrio.IsEmpty && locksHeld >= 0)
        {
            while (!lowerPrio.IsEmpty)
            {
#pragma warning disable CA2000 // Dispose objects before losing scope
                lowerPrio.Dequeue(new LowerPrioKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
                ++locksHeld;
            }
        }
        else if (!higherPrio.IsEmpty)
        {
            --locksHeld;
#pragma warning disable CA2000 // Dispose objects before losing scope
            higherPrio.Dequeue(new HigherPrioKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
            return;
        }
    }

    /// <summary>
    /// Releases the lock as a lowerPrio.
    /// </summary>
    internal void ReleaseLowerPrioLock()
    {
        lock (this)
        {
            --locksHeld;
            ReleaseWaiters();
        }
    }

    /// <summary>
    /// Releases the lock as a HigherPrio.
    /// </summary>
    internal void ReleaseHigherPrioLock()
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
    /// The disposable which releases the lowerPrio lock.
    /// </summary>
    private sealed class LowerPrioKey : IDisposable
    {
        private readonly AsyncPriorityLock asyncPriorityLock;

        /// <summary>
        /// Creates the key for a lock.
        /// </summary>
        /// <param name="asyncPriorityLock">The lock to release. May not be <c>null</c>.</param>
        public LowerPrioKey(AsyncPriorityLock asyncPriorityLock)
        {
            this.asyncPriorityLock = asyncPriorityLock;
        }

        public void Dispose()
        {
            asyncPriorityLock.ReleaseLowerPrioLock();
        }
    }

    /// <summary>
    /// The disposable which releases the higherPrio lock.
    /// </summary>
    private sealed class HigherPrioKey : IDisposable
    {
        private readonly AsyncPriorityLock asyncPriorityLock;

        /// <summary>
        /// Creates the key for a lock.
        /// </summary>
        /// <param name="asyncPriorityLock">The lock to release. May not be <c>null</c>.</param>
        public HigherPrioKey(AsyncPriorityLock asyncPriorityLock)
        {
            this.asyncPriorityLock = asyncPriorityLock;
        }

        public void Dispose()
        {
            asyncPriorityLock.ReleaseHigherPrioLock();
        }
    }
}
