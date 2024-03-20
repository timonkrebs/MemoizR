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

    public static Reaction<T> CreateReaction<T>(this MemoFactory memoFactory, IStateGetR<T> memo, Action<T> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T>(memo, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T> CreateReaction<T>(this MemoFactory memoFactory, string label, IStateGetR<T> memo, Action<T> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T>(memo, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 2 memos
    public static Reaction<T1, T2> CreateReaction<T1, T2>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, Action<T1, T2> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2>(memo1, memo2, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2> CreateReaction<T1, T2>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, Action<T1, T2> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2>(memo1, memo2, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 3 memos
    public static Reaction<T1, T2, T3> CreateReaction<T1, T2, T3>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, Action<T1, T2, T3> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3>(memo1, memo2, memo3, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2, T3> CreateReaction<T1, T2, T3>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, Action<T1, T2, T3> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3>(memo1, memo2, memo3, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 4 memos
    public static Reaction<T1, T2, T3, T4> CreateReaction<T1, T2, T3, T4>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, Action<T1, T2, T3, T4> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4>(memo1, memo2, memo3, memo4, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2, T3, T4> CreateReaction<T1, T2, T3, T4>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, Action<T1, T2, T3, T4> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4>(memo1, memo2, memo3, memo4, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 5 memos
    public static Reaction<T1, T2, T3, T4, T5> CreateReaction<T1, T2, T3, T4, T5>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, Action<T1, T2, T3, T4, T5> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5>(memo1, memo2, memo3, memo4, memo5, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2, T3, T4, T5> CreateReaction<T1, T2, T3, T4, T5>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, Action<T1, T2, T3, T4, T5> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5>(memo1, memo2, memo3, memo4, memo5, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 6 memos
    public static Reaction<T1, T2, T3, T4, T5, T6> CreateReaction<T1, T2, T3, T4, T5, T6>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, Action<T1, T2, T3, T4, T5, T6> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6>(memo1, memo2, memo3, memo4, memo5, memo6, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2, T3, T4, T5, T6> CreateReaction<T1, T2, T3, T4, T5, T6>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, Action<T1, T2, T3, T4, T5, T6> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6>(memo1, memo2, memo3, memo4, memo5, memo6, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 7 memos
    public static Reaction<T1, T2, T3, T4, T5, T6, T7> CreateReaction<T1, T2, T3, T4, T5, T6, T7>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, Action<T1, T2, T3, T4, T5, T6, T7> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6, T7>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2, T3, T4, T5, T6, T7> CreateReaction<T1, T2, T3, T4, T5, T6, T7>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, Action<T1, T2, T3, T4, T5, T6, T7> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6, T7>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 8 memos
    public static Reaction<T1, T2, T3, T4, T5, T6, T7, T8> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, Action<T1, T2, T3, T4, T5, T6, T7, T8> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2, T3, T4, T5, T6, T7, T8> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, Action<T1, T2, T3, T4, T5, T6, T7, T8> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 9 memos
    public static Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    // 10 memos
    public static Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, action, memoFactory.Context, synchronizationContext);
        }
    }

    public static Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(this MemoFactory memoFactory, string label, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, action, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }

    public static AdvancedReaction CreateAdvancedReaction(this MemoFactory memoFactory, Func<CancellationTokenSource, Task> fn)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new AdvancedReaction(fn, memoFactory.Context, synchronizationContext);
        }
    }

    public static AdvancedReaction CreateAdvancedReaction(this MemoFactory memoFactory, string label, Func<CancellationTokenSource, Task> fn)
    {
        lock (memoFactory)
        {
            SynchronizationContexts.TryGetValue(memoFactory, out var synchronizationContext);
            return new AdvancedReaction(fn, memoFactory.Context, synchronizationContext)
            {
                Label = label
            };
        }
    }
}
