using MemoizR.StructuredAsyncLock;
using Nito.AsyncEx;

namespace MemoizR;

public class ReactionScope
{
    internal IMemoHandlR? CurrentReaction = null;
    internal IMemoHandlR[] CurrentGets = [];
    internal int CurrentGetsIndex;
}

public class Context
{
    private readonly Random rand = new();
    private static readonly AsyncLocal<int> AsyncLocalScope = new();
    internal AsyncAsymmetricLock ContextLock = new();
    internal AsyncLock Mutex = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    private Dictionary<int, WeakReference<ReactionScope>> AsyncReactionScopes = new();

    public ReactionScope ReactionScope
    {
        get
        {
            lock (this)
            {
                var key = AsyncLocalScope.Value;
                if(!(AsyncReactionScopes.TryGetValue(key, out var reactionScopeRef) && reactionScopeRef!.TryGetTarget(out var reactionScope)))
                {
                    reactionScope = new ReactionScope();
                    AsyncReactionScopes.Add(key, new WeakReference<ReactionScope>(reactionScope));
                }
   
                return reactionScope;
            }
        }
    }

    public CancellationTokenSource? CancellationTokenSource { get; internal set; }

    internal void CreateNewScopeIfNeeded()
    {
        if (AsyncLocalScope.Value != 0)
        {
            return;
        }
        lock (this)
        {
            var key = rand.Next(1, int.MaxValue);
            AsyncLocalScope.Value = key;
            AsyncReactionScopes.Add(key, new WeakReference<ReactionScope>(new ReactionScope()));
        }
    }

    internal void CleanScope()
    {
        lock (this)
        {
            AsyncReactionScopes.Remove(AsyncLocalScope.Value);
        }
    }

    internal void CheckDependenciesTheSame(IMemoHandlR memoHandlR)
    {
        lock (this)
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
}
