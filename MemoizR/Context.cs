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

    internal AsyncLock Mutex = new();

    /// <summary>
    /// The evaluation scope for the currently executing async flow. It identifies sources
    /// (other memoizR elements) while a memoizR function body is being evaluated, and carries
    /// the <see cref="ReactionScope.ContextLock"/> that serializes evaluation <em>within</em> that flow.
    /// </summary>
    /// <remarks>
    /// The scope — and therefore its ContextLock — is per async-flow: two independent threads/flows
    /// each resolve their own <see cref="ReactionScope"/> with their own lock instance. This lock does
    /// NOT, on its own, serialize graph evaluation across unrelated threads (i.e. it is not a global
    /// "only one thread evaluates the graph at a time" lock — see the threading-model open question in
    /// the code review). Using <see cref="AsyncLocal{T}"/> gives a stable per-flow scope identity and
    /// avoids the unbounded scope leak the previous dictionary-keyed implementation suffered from.
    /// </remarks>
    private static readonly AsyncLocal<ReactionScope?> CurrentScope = new();

    public ReactionScope ReactionScope => CurrentScope.Value ??= new ReactionScope();

    public CancellationTokenSource? CancellationTokenSource { get; internal set; }

    internal void CreateNewScopeIfNeeded()
    {
        CurrentScope.Value ??= new ReactionScope();
    }

    internal void CleanScope()
    {
        CurrentScope.Value = null;
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

    internal void ForceNewScope()
    {
        CurrentScope.Value = new ReactionScope();
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
