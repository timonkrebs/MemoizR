using MemoizR.StructuredAsyncLock;
using Nito.AsyncEx;

namespace MemoizR;

public class ReactionScope
{
    internal volatile IMemoHandlR? CurrentReaction = null;
    internal volatile IMemoHandlR[] CurrentGets = [];
    internal volatile int CurrentGetsIndex;
    internal AsyncAsymmetricLock ContextLock = new();
}

public class Context
{
    private Lock Lock { get; } = new();
    private static readonly Random rand = new();
    private static readonly AsyncLocal<double> AsyncLocalScope = new();
    
    internal AsyncLock Mutex = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    private Dictionary<double, WeakReference<ReactionScope>> AsyncReactionScopes = new();

    public ReactionScope ReactionScope
    {
        get
        {
            lock (Lock)
            {
                var key = AsyncLocalScope.Value == 0 ? rand.NextDouble() : AsyncLocalScope.Value;
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
            if (AsyncLocalScope.Value != 0)
            {
                return;
            }
            var key = rand.NextDouble();
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

    internal double ForceNewScope()
    {
        lock (Lock)
        {
            var key = rand.NextDouble();
            AsyncLocalScope.Value = key;
            AsyncReactionScopes.Add(key, new(new()));
            return key;
        }
    }
}
