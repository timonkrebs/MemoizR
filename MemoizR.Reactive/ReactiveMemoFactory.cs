using MemoizR.Reactive;

namespace MemoizR;

public static class ReactiveMemoFactory
{
    /// <summary>
    /// Pins the side effects of reactions built from this factory to an executor (a UI thread's
    /// SynchronizationContext wrapped in a <see cref="SynchronizationContextExecutor"/>, a
    /// <see cref="DedicatedThreadExecutor"/>, or a custom <see cref="IExecutor"/>) -- the custom
    /// actor executor analog (SE-0392, issue #36). Applies to reactions built AFTER the call.
    /// </summary>
    public static MemoFactory AddExecutor(this MemoFactory memoFactory, IExecutor executor)
    {
        memoFactory.Executor = executor;
        return memoFactory;
    }

    public static MemoFactory AddSynchronizationContext(this MemoFactory memoFactory, SynchronizationContext synchronizationContext)
    {
        memoFactory.Executor = new SynchronizationContextExecutor(synchronizationContext);
        return memoFactory;
    }

    public static ReactionBuilder BuildReaction(this MemoFactory memoFactory, string label = "Reaction")
    {
        return new(memoFactory, memoFactory.Executor, label);
    }
}
