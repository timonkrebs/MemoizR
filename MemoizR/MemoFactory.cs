namespace MemoizR;

public sealed class MemoFactory
{
    private static readonly Lock contextsLock = new();
    internal static Dictionary<string, WeakReference<Context>> CONTEXTS = new Dictionary<string, WeakReference<Context>>();
    internal Context Context { get; }
    internal Lock Lock { get; } = new();

    // The executor reaction side effects built from this factory are marshalled to (set via
    // MemoizR.Reactive's AddExecutor / AddSynchronizationContext, or MemoizR.Wpf's
    // AddWpfDispatcher -- a UI SynchronizationContext arrives wrapped in a
    // SynchronizationContextExecutor): a Reaction enqueues only its action with the
    // already-evaluated dependency values, an AdvancedReaction its whole Execute. Lives on the
    // factory itself so the association is discoverable and dies with the factory -- it
    // previously sat in a static side-table in another assembly, which rooted every registered
    // factory forever.
    internal IExecutor? Executor { get; set; }

    // The TimeProvider reactions built from this factory schedule their debounce delays on
    // (set via MemoizR.Reactive's AddTimeProvider). Null means TimeProvider.System. Tests inject
    // a FakeTimeProvider so debounce windows elapse under test control instead of wall-clock time.
    internal TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Options are per-factory, not per-context: strictness governs how THIS factory creates
    /// nodes, so a strict and a lax factory may deliberately share one keyed context.
    /// </summary>
    public MemoFactoryOptions Options { get; }

    public MemoFactory(string? contextKey = null, MemoFactoryOptions options = MemoFactoryOptions.None)
    {
        Options = options;

        // Default context is mapped to empty string
        if (string.IsNullOrWhiteSpace(contextKey))
        {
            Context = new();
            return;
        }

        lock (contextsLock)
        {
            // The registry holds contexts weakly; sweep dead entries while we are here so it
            // stays bounded by the number of live keyed contexts (CleanUpContexts remains for
            // callers that want an explicit sweep).
            RemoveDeadContexts();

            if (CONTEXTS.TryGetValue(contextKey, out var weakContext))
            {
                if (weakContext.TryGetTarget(out var context))
                {
                    Context = context;
                }
                else
                {
                    Context = new();
                    weakContext.SetTarget(Context);
                }
            }
            else
            {
                Context = new();
                CONTEXTS.Add(contextKey, new(Context));
            }
        }
    }

    public static void CleanUpContexts()
    {
        lock (contextsLock)
        {
            RemoveDeadContexts();
        }
    }

    // Must be called under contextsLock.
    private static void RemoveDeadContexts()
    {
        List<string>? keysToRemove = null;
        foreach (var kvp in CONTEXTS)
        {
            if (!kvp.Value.TryGetTarget(out _))
            {
                (keysToRemove ??= new()).Add(kvp.Key);
            }
        }

        if (keysToRemove != null)
        {
            foreach (var key in keysToRemove)
            {
                CONTEXTS.Remove(key);
            }
        }
    }

    public MemoizR<T> CreateMemoizR<T>(Func<Task<T>> fn)
    {
        return CreateMemoizR(_ => fn());
    }

    public MemoizR<T> CreateMemoizR<T>(string label, Func<Task<T>> fn)
    {
        return CreateMemoizR(label, _ => fn());
    }

    public MemoizR<T> CreateMemoizR<T>(Func<CancellationTokenSource, Task<T>> fn)
    {
        return CreateMemoizR("MemoizR", fn);
    }

    public MemoizR<T> CreateMemoizR<T>(string label, Func<CancellationTokenSource, Task<T>> fn)
    {
        EnsureSendableIfStrict<T>();
        return new(fn, Context)
        {
            Label = label
        };
    }

    public Signal<T> CreateSignal<T>(T value)
    {
        return CreateSignal("Signal", value);
    }

    public Signal<T> CreateSignal<T>(string label, T value)
    {
        EnsureSendableIfStrict<T>();
        return new(value, Context)
        {
            Label = label
        };
    }

    public EagerRelativeSignal<T> CreateEagerRelativeSignal<T>(T value)
    {
        return CreateEagerRelativeSignal("Relative Signal", value);
    }

    public EagerRelativeSignal<T> CreateEagerRelativeSignal<T>(string label, T value)
    {
        EnsureSendableIfStrict<T>();
        return new(value, Context)
        {
            Label = label
        };
    }

    /// <summary>
    /// EXPERIMENTAL (issue #36 layer 5, ADR 0006): creates a signal on the actor engine, where
    /// all graph bookkeeping runs as turns of the context's <see cref="GraphActor"/> instead of
    /// under locks. Actor-engine nodes only interoperate with other actor-engine nodes of the
    /// same context; they cannot be wired into lock-engine memos or reactions (deliberately, at
    /// the type level).
    /// </summary>
    public ActorSignal<T> CreateActorSignal<T>(T value)
    {
        EnsureSendableIfStrict<T>();
        return new(value, Context);
    }

    /// <summary>
    /// EXPERIMENTAL (issue #36 layer 5, ADR 0006): creates a memo on the actor engine. Same
    /// observable semantics as <see cref="CreateMemoizR{T}(Func{Task{T}})"/> -- lazy, dynamic,
    /// generation-guarded -- with every piece of bookkeeping serialized by the context's
    /// <see cref="GraphActor"/>.
    /// </summary>
    public ActorMemo<T> CreateActorMemoizR<T>(Func<Task<T>> fn)
    {
        EnsureSendableIfStrict<T>();
        return new(fn, Context);
    }

    // Strict-mode boundary check (issue #36): every node type whose value crosses flows funnels
    // its creation through this. Internal so the structured-concurrency factory extensions (a
    // friend assembly) enforce the same contract for their nodes.
    internal void EnsureSendableIfStrict<T>()
    {
        if (Options.HasFlag(MemoFactoryOptions.StrictSendableChecks))
        {
            SendableChecker.EnsureSendable(typeof(T));
        }
    }

    /// <summary>
    /// Throws when the current async flow is not inside a MemoizR-serialized graph evaluation
    /// (a Get/Set/recompute or reaction update holding this flow's evaluation lock). The runtime
    /// analog of Swift's <c>preconditionIsolated()</c> (SE-0423): call it from code that must
    /// only ever run inside the graph's isolation, e.g. at the top of a memo's computation
    /// helper that touches state the graph is supposed to serialize.
    /// </summary>
    public void AssertEvaluationIsolated()
    {
        Context.AssertEvaluationIsolated();
    }

    public T Untrack<T>(Func<T> fn)
    {
        return Context.Untrack(fn);
    }

    public Task<T> Untrack<T>(Func<Task<T>> fn)
    {
        return Context.Untrack(fn);
    }
}
