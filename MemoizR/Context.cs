using MemoizR.StructuredAsyncLock;

namespace MemoizR;

public class ReactionScope
{
    // CurrentReaction is read on the lock-free Get fast path (e.g. MemoizR.Get), so it stays
    // volatile for that read's visibility. CurrentGets/CurrentGetsIndex are volatile because no
    // single monitor covers all their accesses: writes go through CheckDependenciesTheSame under
    // Context.Lock, but StructuredReduceJob's parallel children (which share their parent flow's
    // scope -- only StructuredResultsJob forces per-child scopes) read them under the job's own
    // Lock, and the per-flow-reentrant ContextLock grants all those children concurrently, so it
    // serializes none of this. volatile supplies the missing release/acquire pairing (ADR 0001
    // rule 2); writes are whole-array swaps, so the reference publish is atomic.
    internal volatile IMemoHandlR? CurrentReaction = null;
    internal volatile IMemoHandlR[] CurrentGets = [];
    internal volatile int CurrentGetsIndex;
    internal AsyncAsymmetricLock ContextLock = new();
}

public class Context
{
    private Lock Lock { get; } = new();
    private static readonly AsyncLocal<double> AsyncLocalScope = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body
    * ConcurrentDictionary so the hot read paths resolve scopes lock-free; Lock guards only the
    * dependency-capture mutation (CheckDependenciesTheSame) and the evaluation refcount. */
    private readonly System.Collections.Concurrent.ConcurrentDictionary<double, WeakReference<ReactionScope>> AsyncReactionScopes = new();
    private int scopeRegistrationsSinceLastPrune;

    // Whether the current async flow has a pinned scope. An UNPINNED flow's scope would be
    // freshly minted, so its CurrentReaction is null by construction -- which lets the Get fast
    // paths skip scope resolution (and the mint!) entirely for clean reads.
    internal bool HasFlowScope => AsyncLocalScope.Value != 0;

    public ReactionScope ReactionScope
    {
        get
        {
            var key = AsyncLocalScope.Value;
            if (key == 0)
            {
                // No scope is pinned to this flow, so the scope could never be looked up again:
                // registering it would only leak a dictionary entry per access. Hand out a
                // throwaway.
                return new();
            }

            return GetScopeForKey(key);
        }
    }

    // Lock-free resolve-or-resurrect for a pinned key. The resurrection must be atomic: several
    // tasks can share one flow key concurrently (debounced reaction updates inherit the
    // triggering Set's flow), and two of them racing a dead entry must agree on ONE fresh scope
    // -- a last-write-wins overwrite would hand them different ContextLocks.
    private ReactionScope GetScopeForKey(double key)
    {
        if (AsyncReactionScopes.TryGetValue(key, out var reactionScopeRef)
            && reactionScopeRef.TryGetTarget(out var reactionScope))
        {
            return reactionScope;
        }

        ReactionScope fresh = new();
        WeakReference<ReactionScope> freshRef = new(fresh);
        while (true)
        {
            var existing = AsyncReactionScopes.GetOrAdd(key, freshRef);
            if (ReferenceEquals(existing, freshRef))
            {
                return fresh;
            }
            if (existing.TryGetTarget(out var live))
            {
                return live;
            }
            if (AsyncReactionScopes.TryUpdate(key, freshRef, existing))
            {
                return fresh;
            }
        }
    }

    public CancellationTokenSource? CancellationTokenSource { get; private set; }

    private int evaluationDepth;

    // The context-wide CancellationTokenSource is shared by every evaluation in flight (so
    // Cancel() reaches the whole computation tree), so its lifetime must be refcounted: it is
    // created by the first root evaluation to enter and torn down by the LAST one to exit.
    // The previous protocol ("the call that created it nulls it in its finally") lost the source
    // under concurrency: root A creates it, root B enters while it exists, A finishes and nulls
    // the shared field while B is still mid-evaluation -- B's later reads then NRE'd and
    // Cancel() silently stopped working.
    internal void EnterEvaluationScope()
    {
        lock (Lock)
        {
            evaluationDepth++;
            CancellationTokenSource ??= new();
        }
    }

    internal void ExitEvaluationScope()
    {
        lock (Lock)
        {
            if (--evaluationDepth == 0)
            {
                CancellationTokenSource = null;
            }
        }
    }

    // Pins a scope to the current async flow if it has none yet. Returns whether a scope was
    // created: a caller that pairs this with CleanScope must only clean up when it created the
    // scope, otherwise it destroys a live scope an enclosing evaluation on the same flow is
    // still using (its dependency capture would silently resolve to a fresh empty scope).
    internal bool CreateNewScopeIfNeeded()
    {
        if (AsyncLocalScope.Value != 0)
        {
            return false;
        }
        var key = Random.Shared.NextDouble();
        AsyncLocalScope.Value = key;
        RegisterScope(key, new());
        return true;
    }

    // Pins a scope if needed and returns it. Used by the Get slow paths; the resolution is
    // lock-free (ConcurrentDictionary), so this is cheap even under cross-flow contention.
    internal ReactionScope GetOrCreateScope()
    {
        var key = AsyncLocalScope.Value;
        if (key == 0)
        {
            key = Random.Shared.NextDouble();
            AsyncLocalScope.Value = key;
            ReactionScope created = new();
            RegisterScope(key, created);
            return created;
        }

        return GetScopeForKey(key);
    }

    private void RegisterScope(double key, ReactionScope scope)
    {
        AsyncReactionScopes.TryAdd(key, new(scope)); // fresh random key: cannot collide

        // Amortized sweep: pruning on every registration is O(table) per mint, which goes
        // quadratic under sustained traffic from unpinned flows (every top-level operation mints
        // a scope). Sweep only when the registrations since the last sweep rival the table size,
        // making each registration O(1) amortized. Concurrent double-sweeps are harmless.
        var registrations = Interlocked.Increment(ref scopeRegistrationsSinceLastPrune);
        if (registrations >= 64 && registrations >= AsyncReactionScopes.Count / 2)
        {
            Interlocked.Exchange(ref scopeRegistrationsSinceLastPrune, 0);
            PruneDeadScopes();
        }
    }

    // Test hook: the number of registered scope entries (live or dead), for asserting that
    // PruneDeadScopes keeps the registry bounded.
    internal int RegisteredScopeCount => AsyncReactionScopes.Count;

    // The scope targets are weak, but the dictionary entries themselves are not: sweep the dead
    // ones so the map stays bounded by the number of LIVE flows. The conditional TryRemove only
    // removes the exact dead entry, so it can never race away a concurrently resurrected scope.
    internal void PruneDeadScopes()
    {
        foreach (var kvp in AsyncReactionScopes)
        {
            if (!kvp.Value.TryGetTarget(out _))
            {
                ((System.Collections.Generic.ICollection<KeyValuePair<double, WeakReference<ReactionScope>>>)AsyncReactionScopes).Remove(kvp);
            }
        }
    }

    internal void CleanScope()
    {
        AsyncReactionScopes.TryRemove(AsyncLocalScope.Value, out _);
    }

    internal void CheckDependenciesTheSame(IMemoHandlR memoHandlR)
    {
        lock (Lock)
        {
            // Resolve the scope once: every ReactionScope access re-enters this very lock and
            // probes the dictionary, and this method runs on every tracked Get.
            var scope = ReactionScope;
            var noNewGets = scope.CurrentGets.Length == 0;

            var hasEnoughSources = scope.CurrentReaction?.Sources?.Length > 0 && scope.CurrentReaction.Sources.Length >= scope.CurrentGetsIndex + 1;
            var currentSourceEqualsThis = hasEnoughSources && scope.CurrentReaction!.Sources?[scope.CurrentGetsIndex] == memoHandlR;

            if (noNewGets && currentSourceEqualsThis)
            {
                Interlocked.Increment(ref scope.CurrentGetsIndex);
            }
            else
            {
                scope.CurrentGets = [.. scope.CurrentGets, memoHandlR];

                // Subscribe EAGERLY, at capture time, not after the evaluation completes: a Set
                // landing between this read and the deferred link rewiring would otherwise see no
                // observer and notify nobody -- the node would commit a value computed from the
                // pre-Set read with no Stale ever bumping its generation (the first-evaluation
                // subscription window). With the link in place immediately, that Set reaches the
                // node mid-evaluation, the commit is refused, and the normal machinery re-runs.
                // (Prefix-matched re-reads above are already subscribed from the previous run.)
                if (scope.CurrentReaction is IMemoizR reaction
                    && scope.CurrentReaction is SignalHandlR node
                    && !node.IsObserving(memoHandlR))
                {
                    memoHandlR.Observers = [.. memoHandlR.Observers, new(reaction)];
                }
            }
        }
    }

    internal double ForceNewScope()
    {
        var key = Random.Shared.NextDouble();
        AsyncLocalScope.Value = key;
        RegisterScope(key, new());
        return key;
    }

    public T Untrack<T>(Func<T> fn)
    {
        var listener = ReactionScope.CurrentReaction;
        ReactionScope.CurrentReaction = null;
        try
        {
            return fn();
        }
        finally
        {
            ReactionScope.CurrentReaction = listener;
        }
    }

    public async Task<T> Untrack<T>(Func<Task<T>> fn)
    {
        var listener = ReactionScope.CurrentReaction;
        ReactionScope.CurrentReaction = null;
        try
        {
            return await fn();
        }
        finally
        {
            ReactionScope.CurrentReaction = listener;
        }
    }
}
