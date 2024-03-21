namespace MemoizR.Reactive;

public sealed class ReactionBuilder
{
    private readonly MemoFactory memoFactory;
    private readonly SynchronizationContext? synchronizationContext;

    private string label;
    private TimeSpan debounceTime = TimeSpan.FromMilliseconds(10);

    public ReactionBuilder(MemoFactory memoFactory, SynchronizationContext? synchronizationContext, string label)
    {
        this.memoFactory = memoFactory;
        this.synchronizationContext = synchronizationContext;
        this.label = label;
    }

    public ReactionBuilder AddDebounceTime(TimeSpan debounceTime)
    {
        this.debounceTime = debounceTime;
        return this;
    }

    public Reaction<T> CreateReaction<T>(IStateGetR<T> memo, Action<T> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T>(memo, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2> CreateReaction<T1, T2>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, Action<T1, T2> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2>(memo1, memo2, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2, T3> CreateReaction<T1, T2, T3>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, Action<T1, T2, T3> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2, T3>(memo1, memo2, memo3, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2, T3, T4> CreateReaction<T1, T2, T3, T4>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, Action<T1, T2, T3, T4> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2, T3, T4>(memo1, memo2, memo3, memo4, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2, T3, T4, T5> CreateReaction<T1, T2, T3, T4, T5>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, Action<T1, T2, T3, T4, T5> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2, T3, T4, T5>(memo1, memo2, memo3, memo4, memo5, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2, T3, T4, T5, T6> CreateReaction<T1, T2, T3, T4, T5, T6>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, Action<T1, T2, T3, T4, T5, T6> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2, T3, T4, T5, T6>(memo1, memo2, memo3, memo4, memo5, memo6, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2, T3, T4, T5, T6, T7> CreateReaction<T1, T2, T3, T4, T5, T6, T7>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, Action<T1, T2, T3, T4, T5, T6, T7> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2, T3, T4, T5, T6, T7>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2, T3, T4, T5, T6, T7, T8> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, Action<T1, T2, T3, T4, T5, T6, T7, T8> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action)
    {
        lock (memoFactory)
        {
            return new Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, action, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }


    public AdvancedReaction CreateAdvancedReaction(Func<CancellationTokenSource, Task> fn)
    {
        return new AdvancedReaction(fn, memoFactory.Context, synchronizationContext)
        {
            Label = label,
            DebounceTime = debounceTime
        };
    }
}