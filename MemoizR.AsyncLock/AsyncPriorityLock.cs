namespace MemoizR.AsyncLock;

public class AsyncPriorityLock
{
    /// <summary>
    /// The queue of TCSs that other tasks are awaiting to acquire the lock as higherlevel.
    /// </summary>
    IAsyncWaitQueue<IDisposable> higherlevel = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// The queue of TCSs that other tasks are awaiting to acquire the lock as lowerlevel.
    /// </summary>
    IAsyncWaitQueue<IDisposable> lowerlevel = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// Number of lowerlevel locks held; negative if higherlevel lock are held; 0 if no locks are held.
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
    /// Asynchronously acquires the lock as a reader. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestReaderLockAsync(CancellationToken cancellationToken)
    {
        lock (this)
        {
            // If the lock is available or in read mode and there are no waiting writers, upgradeable readers, or upgrading readers, take it immediately.
            if (locksHeld >= 0 && higherlevel.IsEmpty && upgradedLocksHeld == 0)
            {
                ++locksHeld;
                return Task.FromResult<IDisposable>(new ReaderKey(this));
            }
            else
            {
                // Wait for the lock to become available or cancellation.
                return lowerlevel.Enqueue(this, cancellationToken, 0);
            }
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a reader. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> ReaderLockAsync(CancellationToken cancellationToken)
    {
        return new AwaitableDisposable<IDisposable>(RequestReaderLockAsync(cancellationToken));
    }

    /// <summary>
    /// Asynchronously acquires the lock as a reader. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> ReaderLockAsync()
    {
        return ReaderLockAsync(CancellationToken.None);
    }

    /// <summary>
    /// Synchronously acquires the lock as a reader. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable ReaderLock(CancellationToken cancellationToken)
    {
        return RequestReaderLockAsync(cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Synchronously acquires the lock as a reader. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable ReaderLock()
    {
        return ReaderLock(CancellationToken.None);
    }

    /// <summary>
    /// Asynchronously acquires the lock as a writer. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestWriterLockAsync(CancellationToken cancellationToken, int lockScope)
    {
        Task<IDisposable> ret;
        lock (this)
        {
            // If the lock is available, take it immediately.
            if (locksHeld == 0)
            {
                locksHeld = -1;
#pragma warning disable CA2000 // Dispose objects before losing scope
                ret = Task.FromResult<IDisposable>(new WriterKey(this));
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
                ret = Task.FromResult<IDisposable>(new WriterKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
            }
            else if (locksHeld < 0 && this.lockScope == lockScope)
            {
                --locksHeld;
#pragma warning disable CA2000 // Dispose objects before losing scope
                ret = Task.FromResult<IDisposable>(new WriterKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
            }
            else
            {
                // Wait for the lock to become available or cancellation.
                ret = higherlevel.Enqueue(this, cancellationToken, lockScope);
            }
        }

        ReleaseWaitersWhenCanceled(ret);
        return ret;
    }

    /// <summary>
    /// Asynchronously acquires the lock as a writer. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> WriterLockAsync(CancellationToken cancellationToken)
    {
        var lockScope = asyncLocalScope.Value;
        if (lockScope == 0)
        {
            lockScope = rand.Next();
            asyncLocalScope.Value = lockScope;
        }
        return new AwaitableDisposable<IDisposable>(RequestWriterLockAsync(cancellationToken, lockScope));
    }

    /// <summary>
    /// Asynchronously acquires the lock as a writer. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public AwaitableDisposable<IDisposable> WriterLockAsync()
    {
        return WriterLockAsync(CancellationToken.None);
    }

    /// <summary>
    /// Synchronously acquires the lock as a writer. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable WriterLock(CancellationToken cancellationToken)
    {
        return RequestWriterLockAsync(cancellationToken, Thread.CurrentThread.ManagedThreadId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously acquires the lock as a writer. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public IDisposable WriterLock()
    {
        return WriterLock(CancellationToken.None);
    }

    /// <summary>
    /// Grants lock(s) to waiting tasks. This method assumes the sync lock is already held.
    /// </summary>
    private void ReleaseWaiters()
    {
        if (locksHeld == -1)
            return;

        // Give priority to writers, then readers.
        if (!higherlevel.IsEmpty)
        {
            if (locksHeld == 0)
            {
                --locksHeld;
#pragma warning disable CA2000 // Dispose objects before losing scope
                higherlevel.Dequeue(new WriterKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
                return;
            }
            /* there probably should be something like:
             else if(upgradedLocksHeld > 0 && lockscope == ???){
++upgradedLocksHeld;
#pragma warning disable CA2000 // Dispose objects before losing scope
                higherlevel.Dequeue(new WriterKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
                return;
            }*/
        }
        else
        {
            while (!lowerlevel.IsEmpty)
            {
#pragma warning disable CA2000 // Dispose objects before losing scope
                lowerlevel.Dequeue(new ReaderKey(this));
#pragma warning restore CA2000 // Dispose objects before losing scope
                ++locksHeld;
            }
        }
    }

    /// <summary>
    /// Releases the lock as a reader.
    /// </summary>
    internal void ReleaseReaderLock()
    {
        lock (this)
        {
            --locksHeld;
            ReleaseWaiters();
        }
    }

    /// <summary>
    /// Releases the lock as a writer.
    /// </summary>
    internal void ReleaseWriterLock()
    {
        lock (this)
        {
            if (upgradedLocksHeld > 0)
            {
                upgradedLocksHeld--;
            }
            else
            {
                locksHeld++;
            }
            ReleaseWaiters();
            if (locksHeld == 0)
            {
                lockScope = 0;
            }
        }
    }

    /// <summary>
    /// The disposable which releases the reader lock.
    /// </summary>
    private sealed class ReaderKey : IDisposable
    {
        private readonly AsyncPriorityLock asyncPriorityLock;

        /// <summary>
        /// Creates the key for a lock.
        /// </summary>
        /// <param name="asyncPriorityLock">The lock to release. May not be <c>null</c>.</param>
        public ReaderKey(AsyncPriorityLock asyncPriorityLock)
        {
            this.asyncPriorityLock = asyncPriorityLock;
        }

        public void Dispose()
        {
            asyncPriorityLock.ReleaseReaderLock();
        }
    }

    /// <summary>
    /// The disposable which releases the writer lock.
    /// </summary>
    private sealed class WriterKey : IDisposable
    {
        private readonly AsyncPriorityLock asyncPriorityLock;

        /// <summary>
        /// Creates the key for a lock.
        /// </summary>
        /// <param name="asyncPriorityLock">The lock to release. May not be <c>null</c>.</param>
        public WriterKey(AsyncPriorityLock asyncPriorityLock)
        {
            this.asyncPriorityLock = asyncPriorityLock;
        }

        public void Dispose()
        {
            asyncPriorityLock.ReleaseWriterLock();
        }
    }
}
