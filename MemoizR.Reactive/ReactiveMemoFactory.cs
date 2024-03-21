using MemoizR.Reactive;

namespace MemoizR;

public static class ReactiveMemoFactory
{
    private static readonly Dictionary<MemoFactory, SynchronizationContext> SynchronizationContexts = new();

    public static MemoFactory AddSynchronizationContext(this MemoFactory memoFactory, SynchronizationContext synchronizationContext)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.Add(memoFactory, synchronizationContext);
            return memoFactory;
        }
    }

    public static ReactionBuilder BuildReaction(this MemoFactory memoFactory)
    {
        SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
        return new ReactionBuilder(memoFactory, synchronizationContext);
    }
}
