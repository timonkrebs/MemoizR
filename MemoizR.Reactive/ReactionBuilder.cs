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

    // The single construction path for every plain Reaction this builder creates: the eager
    // initial run is scheduled only AFTER the object initializer assigned Label/DebounceTime.
    // Kicking it off from the constructor raced the initializer -- a Set on a freshly captured
    // source reaches IMemoizR.Stale, which reads DebounceTime, and observed the unassigned
    // default. No SynchronizationContext is passed to the Reaction: a Reaction's marshalling is
    // owned by its composed body (the CreateReaction overloads evaluate the dependencies on the
    // calling flow and post only the user action through InvokeActionAsync) -- base-level
    // whole-Execute posting would put graph evaluation back on the context and nest a second
    // post around the action's own. CreateAdvancedReaction below is the path for bodies that
    // must run on the context as a whole.
    private Reaction Build(Func<Task> body)
    {
        lock (memoFactory.Lock)
        {
            var reaction = new Reaction(body, memoFactory.Context)
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

    // Run the already-composed UI action: inline when no SynchronizationContext is configured,
    // otherwise posted to it -- and awaited, so the reaction's update pipeline (link rewiring,
    // state commit, fault handling) still observes an exception the action throws. Unlike
    // ReactionBase.InvokeExecute there is no async void here: the action is synchronous, so the
    // callback completes the TCS exactly once on both the success and the throw path.
    private async Task InvokeActionAsync(Action uiAction)
    {
        if (synchronizationContext == null)
        {
            uiAction();
            return;
        }

        // RunContinuationsAsynchronously: completing the TCS must not run the rest of the update
        // pipeline (link rewiring, state commit, lock releases) inline inside the posted
        // callback -- that work belongs to the update's own flow, and running it on e.g. a UI
        // thread inside the post both blocks that thread and exposes the pipeline to whatever
        // exception handling wraps the context's callbacks.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        synchronizationContext.Post(_ =>
        {
            try
            {
                uiAction();
                tcs.SetResult();
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }, null);

        await tcs.Task;
    }

    // Dependencies are separate parameters (not read inside one opaque body) so they can be
    // evaluated independently of the action: the composed body resolves every dependency on the
    // calling flow -- the thread pool for the debounce-scheduled updates -- and only then runs
    // action(v1, ..) through InvokeActionAsync. With a UI SynchronizationContext registered on
    // the factory this is the WPF contract of #13: graph evaluation never occupies the UI
    // thread; the UI thread only ever sees the action with the already-computed values. A body
    // that must run on the context as a whole belongs in CreateAdvancedReaction instead.

    public Reaction CreateReaction<T>(IStateGetR<T> memo, Action<T> action)
    {
        return Build(async () =>
        {
            var v1 = await memo.Get();
            await InvokeActionAsync(() => action(v1));
        });
    }

    public Reaction CreateReaction<T1, T2>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, Action<T1, T2> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            await InvokeActionAsync(() => action(v1, v2));
        });
    }

    public Reaction CreateReaction<T1, T2, T3>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, Action<T1, T2, T3> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            await InvokeActionAsync(() => action(v1, v2, v3));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, Action<T1, T2, T3, T4> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, Action<T1, T2, T3, T4, T5> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, Action<T1, T2, T3, T4, T5, T6> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, Action<T1, T2, T3, T4, T5, T6, T7> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, Action<T1, T2, T3, T4, T5, T6, T7, T8> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            var v9 = await memo9.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            var v9 = await memo9.Get();
            var v10 = await memo10.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            var v9 = await memo9.Get();
            var v10 = await memo10.Get();
            var v11 = await memo11.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            var v9 = await memo9.Get();
            var v10 = await memo10.Get();
            var v11 = await memo11.Get();
            var v12 = await memo12.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            var v9 = await memo9.Get();
            var v10 = await memo10.Get();
            var v11 = await memo11.Get();
            var v12 = await memo12.Get();
            var v13 = await memo13.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            var v9 = await memo9.Get();
            var v10 = await memo10.Get();
            var v11 = await memo11.Get();
            var v12 = await memo12.Get();
            var v13 = await memo13.Get();
            var v14 = await memo14.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            var v9 = await memo9.Get();
            var v10 = await memo10.Get();
            var v11 = await memo11.Get();
            var v12 = await memo12.Get();
            var v13 = await memo13.Get();
            var v14 = await memo14.Get();
            var v15 = await memo15.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14, v15));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, IStateGetR<T16> memo16, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> action)
    {
        return Build(async () =>
        {
            var v1 = await memo1.Get();
            var v2 = await memo2.Get();
            var v3 = await memo3.Get();
            var v4 = await memo4.Get();
            var v5 = await memo5.Get();
            var v6 = await memo6.Get();
            var v7 = await memo7.Get();
            var v8 = await memo8.Get();
            var v9 = await memo9.Get();
            var v10 = await memo10.Get();
            var v11 = await memo11.Get();
            var v12 = await memo12.Get();
            var v13 = await memo13.Get();
            var v14 = await memo14.Get();
            var v15 = await memo15.Get();
            var v16 = await memo16.Get();
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14, v15, v16));
        });
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
