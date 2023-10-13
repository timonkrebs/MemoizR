namespace MemoizR.Reactive;

public static class ReactiveMemoFactory
{
    private static Dictionary<MemoFactory, SynchronizationContext> synchronizationContexts = new Dictionary<MemoFactory, SynchronizationContext>();

    public static MemoFactory AddSynchronizationContext(this MemoFactory memoFactory, SynchronizationContext synchronizationContext)
    {
        lock (memoFactory)
        {
            synchronizationContexts.Add(memoFactory, synchronizationContext);
            return memoFactory;
        }
    }

    public static Reaction CreateReaction(this MemoFactory memoFactory, Func<Task> fn)
    {
        lock (memoFactory)
        {
            synchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction(fn, memoFactory.context, synchronizationContext);
        }
    }

    public static Reaction CreateReaction(this MemoFactory memoFactory, string label, Func<Task> fn)
    {
        lock (memoFactory)
        {
            synchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction(fn, memoFactory.context, synchronizationContext, label);
        }
    }
}
