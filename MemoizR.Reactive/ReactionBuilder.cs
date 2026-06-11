namespace MemoizR.Reactive;

public sealed class ReactionBuilder
{
    private readonly MemoFactory memoFactory;
    private readonly SynchronizationContext? synchronizationContext;

    private string label;
    private TimeSpan debounceTime = TimeSpan.FromMilliseconds(1);

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

    // The single construction path for every reaction this builder creates: the eager initial
    // run is scheduled only AFTER the object initializer assigned Label/DebounceTime. Kicking it
    // off from the constructor raced the initializer -- a Set on a freshly captured source
    // reaches IMemoizR.Stale, which reads DebounceTime, and observed the unassigned default.
    private Reaction Build(Func<Task> body)
    {
        lock (memoFactory.Lock)
        {
            var reaction = new Reaction(body, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
            reaction.ScheduleInitialRun();
            return reaction;
        }
    }

    private class AsyncEnumerator<T> : IAsyncEnumerator<T>
    {
        private Task<T> nextTask = default!;
        internal required Task<T> GetNext
        {
            get
            {
                return nextTask;
            }
            set
            {
                nextTask = value;
            }
        }

        internal Reaction? Reaction { get; set; }
        public T Current { get; set; } = default!;

        public ValueTask DisposeAsync()
        {
            Reaction?.Dispose();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            Current = await GetNext;
            return true;
        }
    }

    private class AsyncEnumerable<T> : IAsyncEnumerable<T>
    {
        private AsyncEnumerator<T> enumerator;

        public AsyncEnumerable(AsyncEnumerator<T> enumerator)
        {
            this.enumerator = enumerator;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return enumerator;
        }
    }

    public IAsyncEnumerable<T> CreateAsyncEnumerableExperimental<T>(IStateGetR<T> memo)
    {
        var tcs = new TaskCompletionSource<T>();
        var enumerator = new AsyncEnumerator<T> { GetNext = tcs.Task };
        var reaction = Build(async () =>
        {
            var result = await memo.Get();
            var oldTsc = tcs;
            var newTcs = new TaskCompletionSource<T>();
            tcs = newTcs;
            enumerator.GetNext = newTcs.Task;
            oldTsc.SetResult(result);
        });

        enumerator.Reaction = reaction;

        return new AsyncEnumerable<T>(enumerator);
    }

    public Reaction CreateReaction<T>(IStateGetR<T> memo, Action<T> action)
    {
        return Build(async () => action(await memo.Get()));
    }

    public Reaction CreateReaction<T1, T2>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, Action<T1, T2> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, Action<T1, T2, T3> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, Action<T1, T2, T3, T4> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, Action<T1, T2, T3, T4, T5> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, Action<T1, T2, T3, T4, T5, T6> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, Action<T1, T2, T3, T4, T5, T6, T7> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, Action<T1, T2, T3, T4, T5, T6, T7, T8> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get(), await memo13.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get(), await memo13.Get(), await memo14.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get(), await memo13.Get(), await memo14.Get(), await memo15.Get()));
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, IStateGetR<T16> memo16, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> action)
    {
        return Build(async () => action(await memo1.Get(), await memo2.Get(), await memo3.Get(), await memo4.Get(), await memo5.Get(), await memo6.Get(), await memo7.Get(), await memo8.Get(), await memo9.Get(), await memo10.Get(), await memo11.Get(), await memo12.Get(), await memo13.Get(), await memo14.Get(), await memo15.Get(), await memo16.Get()));
    }

    public AdvancedReaction CreateAdvancedReaction(Func<Task> fn)
    {
        lock (memoFactory.Lock)
        {
            var reaction = new AdvancedReaction(fn, memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
            reaction.ScheduleInitialRun();
            return reaction;
        }
    }
}
