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
    /// Only one exclusive lock can be held at a time; exclusive acquisitions queue behind any
    /// held or waiting upgradeable lock. Exclusive holders can not enter upgradeable locks of
    /// another scope; trying to acquire an exclusive lock recursively (or inside an upgradeable
    /// lock of the same scope) throws InvalidOperationException, because it would deadlock.
    /// </summary>
    readonly IAsyncWaitQueue<IDisposable> exclusive = new DefaultAsyncWaitQueue<IDisposable>();

    /// <summary>
    /// Number of exclusive locks held; 0 if none. Upgradeable holds are tracked separately in
    /// <see cref="upgradedLocksHeld"/> (this field never goes negative).
    /// </summary>
    // Every read/write of these fields happens inside lock (Lock) (the LocksHeld/
    // UpgradedLocksHeld/LockScope getters lock for outside readers, and all mutations run under
    // that same lock), so the monitor already provides the fences and atomicity.
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
    /// Returns the current flow's lock scope, minting and pinning a fresh one onto the
    /// AsyncLocal if the flow has none yet. Must be called under <see cref="Lock"/>.
    /// </summary>
    private static double GetOrMintLockScope()
    {
        var lockScope = AsyncLocalScope.Value;
        if (lockScope == 0)
        {
            do
            {
                lockScope = Random.Shared.NextDouble();
            } while (lockScope == 0);
            AsyncLocalScope.Value = lockScope;
        }
        return lockScope;
    }

    /// <summary>
    /// Asynchronously acquires the lock as a exclusive. Returns a disposable that releases the lock when disposed.
    /// Must be called under <see cref="Lock"/>; reads the bookkeeping fields directly for that reason.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestExclusiveLockAsync(double requestScope)
    {
        // Upgradeable holds are counted in upgradedLocksHeld (locksHeld never goes negative), so
        // that is what an exclusive acquire in the same scope must be checked against; an
        // exclusive that waited here would wait for its own flow to release -- a deadlock -- so
        // it throws instead.
        if (lockScope == requestScope && upgradedLocksHeld > 0)
        {
            throw new InvalidOperationException("Can not aquire recursive exclusive lock in the scope of an upgradeable lock");
        }

        if (lockScope == requestScope && locksHeld > 0)
        {
            throw new InvalidOperationException("Can not aquire recursive exclusive locks in the same scope");
        }

        // If the lock is available and there are no waiting upgradeable, or upgrading upgradeable, take it immediately.
        if (locksHeld == 0 && upgradeable.IsEmpty && upgradedLocksHeld == 0)
        {
            locksHeld++;
            lockScope = requestScope;
            return Task.FromResult<IDisposable>(new LockKey(this, isUpgradeable: false));
        }
        else if (lockScope != requestScope)
        {
            // Wait for the lock to become available or cancellation.
            return exclusive.Enqueue(requestScope);
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
        lock (Lock)
        {
            // Return a plain Task<IDisposable> rather than the custom AwaitableDisposable
            // wrapper: Coyote's binary rewriter corrupts custom awaitables (it cannot resolve
            // AwaitableDisposable.GetAwaiter), which made every lock-using test throw
            // MissingMethodException under the CI `coyote rewrite` step.
            return RequestExclusiveLockAsync(GetOrMintLockScope());
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock as a upgradeable. Returns a disposable that releases the lock when disposed.
    /// Must be called under <see cref="Lock"/>; reads the bookkeeping fields directly for that reason.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    private Task<IDisposable> RequestUpgradeableLockAsync(double requestScope)
    {
        if (locksHeld == 0 && upgradedLocksHeld == 0)
        {
            upgradedLocksHeld++;
            lockScope = requestScope;
            return Task.FromResult<IDisposable>(new LockKey(this, isUpgradeable: true));
        }
        else if (lockScope == requestScope)
        {
            // Recursive acquisition within the same flow.
            upgradedLocksHeld++;
            return Task.FromResult<IDisposable>(new LockKey(this, isUpgradeable: true, ignoreDispose: true));
        }

        return upgradeable.Enqueue(requestScope);
    }

    /// <summary>
    /// Asynchronously acquires the lock as a Upgradeable. Returns a disposable that releases the lock when disposed.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    public Task<IDisposable> UpgradeableLockAsync()
    {
        lock (Lock)
        {
            return RequestUpgradeableLockAsync(GetOrMintLockScope());
        }
    }

    /// <summary>
    /// Grants lock(s) to waiting tasks. Must be called under <see cref="Lock"/>.
    /// </summary>
    private void ReleaseWaiters()
    {
        if (!upgradeable.IsEmpty && locksHeld == 0 && upgradedLocksHeld == 0)
        {
            upgradedLocksHeld++;
            // The lock-wide lockScope field is the source of truth for recursion checks.
            // We must NOT touch AsyncLocalScope here: this runs on the *releasing* flow, and
            // AsyncLocal mutations never propagate to the already-suspended waiter being woken.
            // The waiter still carries the scope it set before enqueueing, so writing it here
            // only corrupts the releaser's flow (and could let a later acquire on that flow be
            // falsely granted as recursive).
            lockScope = upgradeable.Dequeue(new LockKey(this, isUpgradeable: true));
        }
        else if (!exclusive.IsEmpty && locksHeld == 0 && upgradedLocksHeld == 0)
        {
            locksHeld++;
            lockScope = exclusive.Dequeue(new LockKey(this, isUpgradeable: false));
        }
    }

    /// <summary>
    /// Releases the lock as a exclusive.
    /// </summary>
    internal void ReleaseExclusiveLock()
    {
        lock (Lock)
        {
            locksHeld--;
            if (locksHeld == 0)
            {
                lockScope = 0;
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
            if (upgradedLocksHeld > 0)
            {
                upgradedLocksHeld--;
                if (upgradedLocksHeld == 0 && locksHeld == 0)
                {
                    lockScope = 0;
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
    /// The disposable that releases a held lock. One class serves both modes: an exclusive key
    /// releases via <see cref="ReleaseExclusiveLock"/>, an upgradeable key via
    /// <see cref="ReleaseUpgradeableLock"/> (recursive upgradeable grants pass ignoreDispose so
    /// the nested release does not wake waiters).
    /// </summary>
    private sealed class LockKey : IDisposable
    {
        private readonly AsyncAsymmetricLock asyncLock;
        private readonly bool isUpgradeable;
        private readonly bool ignoreDispose;

        public LockKey(AsyncAsymmetricLock asyncLock, bool isUpgradeable, bool ignoreDispose = false)
        {
            this.asyncLock = asyncLock;
            this.isUpgradeable = isUpgradeable;
            this.ignoreDispose = ignoreDispose;
        }

        public void Dispose()
        {
            if (isUpgradeable)
            {
                asyncLock.ReleaseUpgradeableLock(ignoreDispose);
            }
            else
            {
                asyncLock.ReleaseExclusiveLock();
            }
        }
    }
}
