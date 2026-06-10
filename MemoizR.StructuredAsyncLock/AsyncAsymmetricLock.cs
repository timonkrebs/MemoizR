using MemoizR.StructuredAsyncLock.Nito;

namespace MemoizR.StructuredAsyncLock;

public sealed class AsyncAsymmetricLock
{
    private Lock Lock { get; } = new();
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
    /// Number of exclusive locks held; 0 if none. Upgradeable holds are tracked separately in
    /// <see cref="upgradedLocksHeld"/> (this field never goes negative).
    /// </summary>
    // Every read/write of these counters happens inside lock (Lock) (the LocksHeld/
    // UpgradedLocksHeld getters lock, and the Interlocked mutations run under that same lock),
    // so the monitor already provides the fences and atomicity. No `volatile` is needed.
    private int locksHeld;
    private int upgradedLocksHeld;
    private double lockScope;
    private static readonly AsyncLocal<double> AsyncLocalScope = new();

    internal double LockScope
    {
        get
        {
            lock (Lock)
            {
                return lockScope;
            }
        }
        set
        {
            lock (Lock)
            {
                lockScope = value;
            }
        }
    }

    internal int LocksHeld
    {
        get
        {
            lock (Lock)
            {
                return locksHeld;
            }
        }
    }

    internal int UpgradedLocksHeld
    {
        get
        {
            lock (Lock)
            {
                return upgradedLocksHeld;
            }
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestExclusiveLockAsync(double lockScope)
    {
        // Upgradeable holds are counted in upgradedLocksHeld (locksHeld never goes negative), so
        // that is what an exclusive acquire in the same scope must be checked against; checking
        // `LocksHeld < 0` here was dead code that let this case fall through to the generic
        // "Should never happen!" invariant branch.
        if (LockScope == lockScope && UpgradedLocksHeld > 0)
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
            Interlocked.Increment(ref locksHeld);
            LockScope = lockScope;
            return Task.FromResult<IDisposable>(new ExclusivePrioKey(this));
        }
        else if (LockScope != lockScope)
        {
            // Wait for the lock to become available or cancellation.
            return exclusive.Enqueue(lockScope);
        }
        else
        {
            throw new InvalidOperationException("Should never happen!");
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public Task<IDisposable> ExclusiveLockAsync()
    {
        double lockScope;
        lock (Lock)
        {
            lockScope = AsyncLocalScope.Value;
            if (lockScope == 0)
            {
                do
                {
                    lockScope = Random.Shared.NextDouble();
                } while (lockScope == 0);
                AsyncLocalScope.Value = lockScope;
            }
            // Return a plain Task<IDisposable> rather than the custom AwaitableDisposable
            // wrapper: Coyote's binary rewriter corrupts custom awaitables (it cannot resolve
            // AwaitableDisposable.GetAwaiter), which made every lock-using test throw
            // MissingMethodException under the CI `coyote rewrite` step.
            return RequestExclusiveLockAsync(lockScope);
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a upgradeable. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestUpgradeableLockAsync(double lockScope)
    {
        if (LocksHeld == 0 && UpgradedLocksHeld == 0)
        {
            Interlocked.Increment(ref upgradedLocksHeld);
            LockScope = lockScope;
            return Task.FromResult<IDisposable>(new UpgradeableKey(this, false));
        }
        else if (LockScope == lockScope)
        {
            Interlocked.Increment(ref upgradedLocksHeld);
            return Task.FromResult<IDisposable>(new UpgradeableKey(this, true));
        }

        return upgradeable.Enqueue(lockScope);
    }

    /// <summary>
    /// Asynchronously acquires the lock as a Upgradeable. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public Task<IDisposable> UpgradeableLockAsync()
    {
        double lockScope;
        lock (Lock)
        {
            lockScope = AsyncLocalScope.Value;
            if (lockScope == 0)
            {
                do
                {
                    lockScope = Random.Shared.NextDouble();
                } while (lockScope == 0);
                AsyncLocalScope.Value = lockScope;
            }
            return RequestUpgradeableLockAsync(lockScope);
        }
    }

    /// <summary>
    /// Grants lock(s) to waiting tasks. This method assumes the sync lock is already held.
    /// </summary>
    public void ReleaseWaiters()
    {
        if (!upgradeable.IsEmpty && LocksHeld == 0 && UpgradedLocksHeld == 0)
        {
            Interlocked.Increment(ref upgradedLocksHeld);
            // The lock-wide LockScope field is the source of truth for recursion checks.
            // We must NOT touch AsyncLocalScope here: this runs on the *releasing* flow, and
            // AsyncLocal mutations never propagate to the already-suspended waiter being woken.
            // The waiter still carries the scope it set before enqueueing, so writing it here
            // only corrupts the releaser's flow (and could let a later acquire on that flow be
            // falsely granted as recursive).
            LockScope = upgradeable.Dequeue(new UpgradeableKey(this, false));
        }
        else if (!exclusive.IsEmpty && LocksHeld == 0 && UpgradedLocksHeld == 0)
        {
            Interlocked.Increment(ref locksHeld);
            LockScope = exclusive.Dequeue(new ExclusivePrioKey(this));
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
        lock (Lock)
        {
            Interlocked.Decrement(ref locksHeld);
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
    internal void ReleaseUpgradeableLock(bool doNotRelease)
    {
        lock (Lock)
        {
            if (UpgradedLocksHeld > 0)
            {
                Interlocked.Decrement(ref upgradedLocksHeld);
                if (UpgradedLocksHeld == 0 && LocksHeld == 0)
                {
                    LockScope = 0;
                }
            }
            else
            {
                throw new InvalidOperationException("Should never happen!");
            }
            if (!doNotRelease)
            {
                ReleaseWaiters();
            }

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
        private readonly bool ignoreDispose;

        /// <summary>
        /// Creates the key for a lock.
        /// </summary>
        /// <param name="asyncPriorityLock">The lock to release. May not be <c>null</c>.</param>
        public UpgradeableKey(AsyncAsymmetricLock asyncPriorityLock, bool ignoreDispose)
        {
            this.asyncPriorityLock = asyncPriorityLock;
            this.ignoreDispose = ignoreDispose;
        }

        public void Dispose()
        {
            asyncPriorityLock.ReleaseUpgradeableLock(ignoreDispose);
        }
    }
}
