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
    * - active while evaluating a memoizR function body  */
    private Dictionary<double, WeakReference<ReactionScope>> AsyncReactionScopes = new();

    public ReactionScope ReactionScope
    {
        get
        {
            if (AsyncLocalScope.Value == 0)
            {
                // No scope is pinned to this flow, so the scope could never be looked up again:
                // registering it would only leak a dictionary entry per access. Hand out a
                // throwaway (no lock needed -- nothing shared is touched).
                return new();
            }

            lock (Lock)
            {
                var key = AsyncLocalScope.Value;
                if (AsyncReactionScopes.TryGetValue(key, out var reactionScopeRef)
                    && reactionScopeRef.TryGetTarget(out var reactionScope))
                {
                    return reactionScope;
                }

                ReactionScope fresh = new();
                AsyncReactionScopes[key] = new(fresh);
                return fresh;
            }
        }
    }

    public CancellationTokenSource? CancellationTokenSource { get; internal set; }

    // Pins a scope to the current async flow if it has none yet. Returns whether a scope was
    // created: a caller that pairs this with CleanScope must only clean up when it created the
    // scope, otherwise it destroys a live scope an enclosing evaluation on the same flow is
    // still using (its dependency capture would silently resolve to a fresh empty scope).
    internal bool CreateNewScopeIfNeeded()
    {
        lock (Lock)
        {
            if (AsyncLocalScope.Value != 0)
            {
                return false;
            }
            var key = Random.Shared.NextDouble();
            AsyncLocalScope.Value = key;
            PruneDeadScopes();
            AsyncReactionScopes.Add(key, new(new()));
            return true;
        }
    }

    // Pins a scope if needed and returns it -- one lock acquisition where CreateNewScopeIfNeeded
    // followed by the ReactionScope getter would take two. Used by the Get fast paths, where the
    // context-wide Lock is the contention point.
    internal ReactionScope GetOrCreateScope()
    {
        lock (Lock)
        {
            var key = AsyncLocalScope.Value;
            if (key == 0)
            {
                key = Random.Shared.NextDouble();
                AsyncLocalScope.Value = key;
                PruneDeadScopes();
                ReactionScope created = new();
                AsyncReactionScopes.Add(key, new(created));
                return created;
            }

            if (AsyncReactionScopes.TryGetValue(key, out var reactionScopeRef)
                && reactionScopeRef.TryGetTarget(out var reactionScope))
            {
                return reactionScope;
            }

            ReactionScope fresh = new();
            AsyncReactionScopes[key] = new(fresh);
            return fresh;
        }
    }

    // The scope targets are weak, but the dictionary entries themselves are not: sweep dead ones
    // whenever a new scope is registered so the map stays bounded by the number of LIVE flows.
    // Must be called under Lock.
    private void PruneDeadScopes()
    {
        List<double>? deadKeys = null;
        foreach (var kvp in AsyncReactionScopes)
        {
            if (!kvp.Value.TryGetTarget(out _))
            {
                (deadKeys ??= new()).Add(kvp.Key);
            }
        }

        if (deadKeys != null)
        {
            foreach (var key in deadKeys)
            {
                AsyncReactionScopes.Remove(key);
            }
        }
    }

    internal void CleanScope()
    {
        lock (Lock)
        {
            AsyncReactionScopes.Remove(AsyncLocalScope.Value);
        }
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
            }
        }
    }

    internal double ForceNewScope()
    {
        lock (Lock)
        {
            var key = Random.Shared.NextDouble();
            AsyncLocalScope.Value = key;
            PruneDeadScopes();
            AsyncReactionScopes.Add(key, new(new()));
            return key;
        }
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
