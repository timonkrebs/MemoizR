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
            lock (Lock)
            {
                var key = AsyncLocalScope.Value == 0 ? Random.Shared.NextDouble() : AsyncLocalScope.Value;
                ReactionScope reactionScope;
                if (!AsyncReactionScopes.TryGetValue(key, out var reactionScopeRef))
                {
                    reactionScope = new();
                    AsyncReactionScopes.Add(key, new(reactionScope));
                }
                else if (!reactionScopeRef!.TryGetTarget(out reactionScope!))
                {
                    reactionScope = new();
                    AsyncReactionScopes[key] = new(reactionScope);
                }

                return reactionScope;
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
            AsyncReactionScopes.Add(key, new(new()));
            return true;
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
            var hasCurrentGets = ReactionScope.CurrentGets.Length == 0;

            var hasEnoughSources = ReactionScope.CurrentReaction?.Sources?.Length > 0 && ReactionScope.CurrentReaction.Sources.Length >= ReactionScope.CurrentGetsIndex + 1;
            var currentSourceEqualsThis = hasEnoughSources && ReactionScope.CurrentReaction!.Sources?[ReactionScope.CurrentGetsIndex] == memoHandlR;

            if (hasCurrentGets && currentSourceEqualsThis)
            {
                Interlocked.Increment(ref ReactionScope.CurrentGetsIndex);
            }
            else
            {
                ReactionScope.CurrentGets = !ReactionScope.CurrentGets.Any()
                    ? [memoHandlR]
                    : [.. ReactionScope.CurrentGets, memoHandlR];
            }
        }
    }

    internal double ForceNewScope()
    {
        lock (Lock)
        {
            var key = Random.Shared.NextDouble();
            AsyncLocalScope.Value = key;
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
