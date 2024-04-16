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

    public Reaction CreateReaction<T>(IStateGetR<T> memo, Action<T> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, Action<T1, T2> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, Action<T1, T2, T3> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, Action<T1, T2, T3, T4> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, Action<T1, T2, T3, T4, T5> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, Action<T1, T2, T3, T4, T5, T6> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, Action<T1, T2, T3, T4, T5, T6, T7> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, Action<T1, T2, T3, T4, T5, T6, T7, T8> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get(), await memo13.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get(), await memo13.Get(), await memo14.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get(), await memo13.Get(), await memo14.Get(), await memo15.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, IStateGetR<T16> memo16, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> action)
    {
        lock (memoFactory)
        {
            return new Reaction(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get(), await memo13.Get(), await memo14.Get(), await memo15.Get(), await memo16.Get()), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

    public AdvancedReaction CreateAdvancedReaction(Func<Task> fn)
    {
        return new AdvancedReaction(fn, memoFactory.Context, synchronizationContext)
        {
            Label = label,
            DebounceTime = debounceTime
        };
    }
}
