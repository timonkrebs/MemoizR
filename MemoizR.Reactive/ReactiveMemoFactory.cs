using MemoizR.Reactive;

namespace MemoizR;

public static class ReactiveMemoFactory
{
    public static MemoFactory AddSynchronizationContext(this MemoFactory memoFactory, SynchronizationContext synchronizationContext)
    {
        memoFactory.SynchronizationContext = synchronizationContext;
        return memoFactory;
    }

    public static ReactionBuilder BuildReaction(this MemoFactory memoFactory, string label = "Reaction")
    {
        return new(memoFactory, memoFactory.SynchronizationContext, label);
    }
}
