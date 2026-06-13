namespace MemoizR.Reactive;

public sealed partial class ReactionBuilder
{
    private readonly MemoFactory memoFactory;
    private readonly SynchronizationContext? synchronizationContext;

    private string label;
    private TimeSpan debounceTime = TimeSpan.FromMilliseconds(1);
    // Captured from the factory at build time (mirroring the SynchronizationContext contract):
    // an AddTimeProvider on the factory after BuildReaction does not affect this builder. Null
    // means TimeProvider.System.
    private TimeProvider? timeProvider;

    public ReactionBuilder(MemoFactory memoFactory, SynchronizationContext? synchronizationContext, string label)
    {
        this.memoFactory = memoFactory;
        this.synchronizationContext = synchronizationContext;
        this.label = label;
        this.timeProvider = memoFactory.TimeProvider;
    }

    public ReactionBuilder AddDebounceTime(TimeSpan debounceTime)
    {
        this.debounceTime = debounceTime;
        return this;
    }

    // Per-reaction override of the factory-wide TimeProvider: the debounce delay of reactions
    // created from this builder runs on the given clock (e.g. a FakeTimeProvider in tests).
    public ReactionBuilder AddTimeProvider(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
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
            var reaction = new Reaction(body, memoFactory.Context, timeProvider)
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
    // generation guards). The evaluation runs on a DETACHED task: ForceNewScope overwrites the
    // ambient scope key of whichever flow runs it, and the reaction's update flow re-resolves
    // its own scope by that key after Execute returns (UpdateSourceAndObserverLinks) -- pinning
    // on a detached flow keeps the overwrite structurally local instead of leaning on the async
    // method builder's ExecutionContext restore. It also starts the evaluations truly in
    // parallel: inline, the synchronous prefix of every Get would run sequentially on the
    // calling thread. The local is the weakly-registered scope's only strong root.
    private Task<T> EvaluateOnOwnScopeAsync<T>(IStateGetR<T> memo)
    {
        return Task.Run(async () =>
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
        });
    }

    // The strongly-typed CreateReaction<T1, ..., Tn> overloads (n = 1..16) live in the generated
    // half of this partial class, emitted by MemoizR.Reactive.SourceGenerator (which replaced
    // GenerateReactionFactories.ps1). They build on the helpers above with this contract:
    // dependencies are separate parameters (not reads inside one opaque body) so they can be
    // evaluated independently of each other and of the action (#13): every dependency is
    // registered up front in parameter order (RegisterDependency), the values are then computed
    // IN PARALLEL on isolated scopes (EvaluateOnOwnScopeAsync) -- a dirty dependency costs the
    // slowest recompute instead of the sum -- and only action(v1, ..) is marshalled to the
    // SynchronizationContext when the factory carries one: with MemoizR.Wpf the UI thread sees
    // nothing but the action with the already-computed values. A body that must run on the
    // context as a whole belongs in CreateAdvancedReaction instead.

    public AdvancedReaction CreateAdvancedReaction(Func<Task> fn)
    {
        lock (memoFactory.Lock)
        {
            var reaction = new AdvancedReaction(fn, memoFactory.Context, synchronizationContext, timeProvider)
            {
                Label = label,
                DebounceTime = debounceTime
            };
            reaction.ScheduleInitialRun();
            return reaction;
        }
    }
}
