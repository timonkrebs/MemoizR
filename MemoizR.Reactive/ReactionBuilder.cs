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
    // thread pool and post only the user action through InvokeActionAsync) -- base-level
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

    // Register one dependency in the reaction's capture: deterministic parameter-order tracking
    // plus the eager observer subscription a tracked Get would perform, minus the evaluation.
    // Performed for ALL parameters BEFORE the parallel evaluation starts, so a Set landing
    // mid-evaluation already sees this reaction as observer and refuses the stale commit, and a
    // faulting first run still leaves every parameter wired (the sequential composition only
    // wired the prefix read before the fault). Non-graph IStateGetR implementations are skipped,
    // exactly as their untracked Get would be.
    private void RegisterDependency(object dependency)
    {
        if (dependency is IMemoHandlR handler)
        {
            memoFactory.Context.CheckDependenciesTheSame(handler);
        }
    }

    // Evaluate one dependency on its own pinned scope. The parallel sibling evaluations and the
    // reaction's own update flow must not share a ReactionScope: concurrent evaluation on a
    // single scope corrupts dependency capture (see ReactionBase.RunDebouncedUpdateAsync), while
    // per-evaluation scopes make this exactly the supported concurrent-root-Gets pattern --
    // dirty dependencies recompute in isolation under the cross-flow rules (per-node mutex,
    // generation guards). The local is the weakly-registered scope's only strong root.
    private async Task<T> EvaluateOnOwnScopeAsync<T>(IStateGetR<T> memo)
    {
        var scope = memoFactory.Context.ForceNewScope();
        try
        {
            return await memo.Get();
        }
        finally
        {
            memoFactory.Context.CleanScope();
            GC.KeepAlive(scope);
        }
    }

    // Dependencies are separate parameters (not reads inside one opaque body) so they can be
    // evaluated independently of each other and of the action (#13): every dependency is
    // registered up front in parameter order (RegisterDependency), the values are then computed
    // IN PARALLEL on isolated scopes (EvaluateOnOwnScopeAsync) -- a dirty dependency costs the
    // slowest recompute instead of the sum -- and only action(v1, ..) is marshalled to the
    // SynchronizationContext when the factory carries one: with MemoizR.Wpf the UI thread sees
    // nothing but the action with the already-computed values. A body that must run on the
    // context as a whole belongs in CreateAdvancedReaction instead.

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
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            await Task.WhenAll(t1, t2);
            var v1 = await t1;
            var v2 = await t2;
            await InvokeActionAsync(() => action(v1, v2));
        });
    }

    public Reaction CreateReaction<T1, T2, T3>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, Action<T1, T2, T3> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            await Task.WhenAll(t1, t2, t3);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            await InvokeActionAsync(() => action(v1, v2, v3));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, Action<T1, T2, T3, T4> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            await Task.WhenAll(t1, t2, t3, t4);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            await InvokeActionAsync(() => action(v1, v2, v3, v4));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, Action<T1, T2, T3, T4, T5> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            await Task.WhenAll(t1, t2, t3, t4, t5);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, Action<T1, T2, T3, T4, T5, T6> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, Action<T1, T2, T3, T4, T5, T6, T7> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, Action<T1, T2, T3, T4, T5, T6, T7, T8> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            RegisterDependency(memo9);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            var t9 = EvaluateOnOwnScopeAsync(memo9);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            var v9 = await t9;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            RegisterDependency(memo9);
            RegisterDependency(memo10);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            var t9 = EvaluateOnOwnScopeAsync(memo9);
            var t10 = EvaluateOnOwnScopeAsync(memo10);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9, t10);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            var v9 = await t9;
            var v10 = await t10;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            RegisterDependency(memo9);
            RegisterDependency(memo10);
            RegisterDependency(memo11);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            var t9 = EvaluateOnOwnScopeAsync(memo9);
            var t10 = EvaluateOnOwnScopeAsync(memo10);
            var t11 = EvaluateOnOwnScopeAsync(memo11);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            var v9 = await t9;
            var v10 = await t10;
            var v11 = await t11;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            RegisterDependency(memo9);
            RegisterDependency(memo10);
            RegisterDependency(memo11);
            RegisterDependency(memo12);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            var t9 = EvaluateOnOwnScopeAsync(memo9);
            var t10 = EvaluateOnOwnScopeAsync(memo10);
            var t11 = EvaluateOnOwnScopeAsync(memo11);
            var t12 = EvaluateOnOwnScopeAsync(memo12);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11, t12);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            var v9 = await t9;
            var v10 = await t10;
            var v11 = await t11;
            var v12 = await t12;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            RegisterDependency(memo9);
            RegisterDependency(memo10);
            RegisterDependency(memo11);
            RegisterDependency(memo12);
            RegisterDependency(memo13);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            var t9 = EvaluateOnOwnScopeAsync(memo9);
            var t10 = EvaluateOnOwnScopeAsync(memo10);
            var t11 = EvaluateOnOwnScopeAsync(memo11);
            var t12 = EvaluateOnOwnScopeAsync(memo12);
            var t13 = EvaluateOnOwnScopeAsync(memo13);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11, t12, t13);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            var v9 = await t9;
            var v10 = await t10;
            var v11 = await t11;
            var v12 = await t12;
            var v13 = await t13;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            RegisterDependency(memo9);
            RegisterDependency(memo10);
            RegisterDependency(memo11);
            RegisterDependency(memo12);
            RegisterDependency(memo13);
            RegisterDependency(memo14);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            var t9 = EvaluateOnOwnScopeAsync(memo9);
            var t10 = EvaluateOnOwnScopeAsync(memo10);
            var t11 = EvaluateOnOwnScopeAsync(memo11);
            var t12 = EvaluateOnOwnScopeAsync(memo12);
            var t13 = EvaluateOnOwnScopeAsync(memo13);
            var t14 = EvaluateOnOwnScopeAsync(memo14);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11, t12, t13, t14);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            var v9 = await t9;
            var v10 = await t10;
            var v11 = await t11;
            var v12 = await t12;
            var v13 = await t13;
            var v14 = await t14;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            RegisterDependency(memo9);
            RegisterDependency(memo10);
            RegisterDependency(memo11);
            RegisterDependency(memo12);
            RegisterDependency(memo13);
            RegisterDependency(memo14);
            RegisterDependency(memo15);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            var t9 = EvaluateOnOwnScopeAsync(memo9);
            var t10 = EvaluateOnOwnScopeAsync(memo10);
            var t11 = EvaluateOnOwnScopeAsync(memo11);
            var t12 = EvaluateOnOwnScopeAsync(memo12);
            var t13 = EvaluateOnOwnScopeAsync(memo13);
            var t14 = EvaluateOnOwnScopeAsync(memo14);
            var t15 = EvaluateOnOwnScopeAsync(memo15);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11, t12, t13, t14, t15);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            var v9 = await t9;
            var v10 = await t10;
            var v11 = await t11;
            var v12 = await t12;
            var v13 = await t13;
            var v14 = await t14;
            var v15 = await t15;
            await InvokeActionAsync(() => action(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14, v15));
        });
    }

    public Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, IStateGetR<T16> memo16, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> action)
    {
        return Build(async () =>
        {
            RegisterDependency(memo1);
            RegisterDependency(memo2);
            RegisterDependency(memo3);
            RegisterDependency(memo4);
            RegisterDependency(memo5);
            RegisterDependency(memo6);
            RegisterDependency(memo7);
            RegisterDependency(memo8);
            RegisterDependency(memo9);
            RegisterDependency(memo10);
            RegisterDependency(memo11);
            RegisterDependency(memo12);
            RegisterDependency(memo13);
            RegisterDependency(memo14);
            RegisterDependency(memo15);
            RegisterDependency(memo16);
            var t1 = EvaluateOnOwnScopeAsync(memo1);
            var t2 = EvaluateOnOwnScopeAsync(memo2);
            var t3 = EvaluateOnOwnScopeAsync(memo3);
            var t4 = EvaluateOnOwnScopeAsync(memo4);
            var t5 = EvaluateOnOwnScopeAsync(memo5);
            var t6 = EvaluateOnOwnScopeAsync(memo6);
            var t7 = EvaluateOnOwnScopeAsync(memo7);
            var t8 = EvaluateOnOwnScopeAsync(memo8);
            var t9 = EvaluateOnOwnScopeAsync(memo9);
            var t10 = EvaluateOnOwnScopeAsync(memo10);
            var t11 = EvaluateOnOwnScopeAsync(memo11);
            var t12 = EvaluateOnOwnScopeAsync(memo12);
            var t13 = EvaluateOnOwnScopeAsync(memo13);
            var t14 = EvaluateOnOwnScopeAsync(memo14);
            var t15 = EvaluateOnOwnScopeAsync(memo15);
            var t16 = EvaluateOnOwnScopeAsync(memo16);
            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11, t12, t13, t14, t15, t16);
            var v1 = await t1;
            var v2 = await t2;
            var v3 = await t3;
            var v4 = await t4;
            var v5 = await t5;
            var v6 = await t6;
            var v7 = await t7;
            var v8 = await t8;
            var v9 = await t9;
            var v10 = await t10;
            var v11 = await t11;
            var v12 = await t12;
            var v13 = await t13;
            var v14 = await t14;
            var v15 = await t15;
            var v16 = await t16;
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
