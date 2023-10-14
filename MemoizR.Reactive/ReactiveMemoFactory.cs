using MemoizR.Reactive;

namespace MemoizR;

public static class ReactiveMemoFactory
{
    private static readonly Dictionary<MemoFactory, SynchronizationContext> SynchronizationContexts = new ();

    public static MemoFactory AddSynchronizationContext(this MemoFactory memoFactory, SynchronizationContext synchronizationContext)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.Add(memoFactory, synchronizationContext);
            return memoFactory;
        }
    }

    public static Reaction CreateReaction(this MemoFactory memoFactory, Func<Task> fn)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction(fn, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction CreateReaction(this MemoFactory memoFactory, string label, Func<Task> fn)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction(fn, memoFactory.Context, synchronizationContext, label);
        }
    }
}
