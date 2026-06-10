using MemoizR.StructuredAsyncLock;

namespace MemoizR;

public class ReactionScope
{
    // CurrentReaction is read on the lock-free Get fast path (e.g. MemoizR.Get), so it stays
    // volatile for that read's visibility. CurrentGets/CurrentGetsIndex are only ever touched
    // while the graph is serialized by the ContextLock (and CheckDependenciesTheSame's
    // Context.Lock / Interlocked), so they don't need volatile.
    internal volatile IMemoHandlR? CurrentReaction = null;
    internal IMemoHandlR[] CurrentGets = [];
    internal int CurrentGetsIndex;
    internal AsyncAsymmetricLock ContextLock = new();
}

public class Context
{
    private Lock Lock { get; } = new();
    private static readonly AsyncLocal<Guid> AsyncLocalScope = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    private Dictionary<Guid, WeakReference<ReactionScope>> AsyncReactionScopes = new();

    public ReactionScope ReactionScope
    {
        get
        {
            lock (Lock)
            {
                var key = AsyncLocalScope.Value == Guid.Empty ? Guid.NewGuid() : AsyncLocalScope.Value;
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

    internal void CreateNewScopeIfNeeded()
    {
        lock (Lock)
        {
            if (AsyncLocalScope.Value != Guid.Empty)
            {
                return;
            }
            var key = Guid.NewGuid();
            AsyncLocalScope.Value = key;
            AsyncReactionScopes.Add(key, new(new()));
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

    internal Guid ForceNewScope()
    {
        lock (Lock)
        {
            var key = Guid.NewGuid();
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
