namespace MemoizR;

[Sendable] // internally synchronized by design: safe to share across flows (and to hold in statics, see MZR004)
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
        : this(contextKey, 1, int.MaxValue, options)
    {
    }

    // Pins this factory's context to the node-id slice [idRangeStart, idRangeEnd): distributed
    // peers use disjoint slices so causality stamps merged across peers can never collide on an
    // id, and a contiguous slice keeps merged stamps compact (see
    // docs/architecture/causality-trigger-clock.md). Rebinding an existing context key to a
    // different slice is a configuration conflict and throws.
    public MemoFactory(string? contextKey, int idRangeStart, int idRangeEnd = int.MaxValue, MemoFactoryOptions options = MemoFactoryOptions.None)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(idRangeStart);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idRangeEnd, idRangeStart);
        Options = options;
        var stampsEnabled = !options.HasFlag(MemoFactoryOptions.DisableCausalityStamps);

        // Default context is mapped to empty string
        if (string.IsNullOrWhiteSpace(contextKey))
        {
            Context = new(idRangeStart, idRangeEnd, stampsEnabled);
            return;
        }

        lock (contextsLock)
        {
            // The registry holds contexts weakly; sweep dead entries while we are here so it
            // stays bounded by the number of live keyed contexts (CleanUpContexts remains for
            // callers that want an explicit sweep).
            RemoveDeadContexts();
            Context = ResolveKeyedContext(contextKey, idRangeStart, idRangeEnd, stampsEnabled);
        }
    }

    // Must be called under contextsLock.
    private static Context ResolveKeyedContext(string contextKey, int idRangeStart, int idRangeEnd, bool stampsEnabled)
    {
        if (!CONTEXTS.TryGetValue(contextKey, out var weakContext))
        {
            Context created = new(idRangeStart, idRangeEnd, stampsEnabled);
            CONTEXTS.Add(contextKey, new(created));
            return created;
        }

        if (!weakContext.TryGetTarget(out var context))
        {
            Context resurrected = new(idRangeStart, idRangeEnd, stampsEnabled);
            weakContext.SetTarget(resurrected);
            return resurrected;
        }

        if (context.IdRangeStart != idRangeStart || context.IdRangeEnd != idRangeEnd)
        {
            throw new ArgumentException(
                $"Context key '{contextKey}' is already bound to the node-id slice [{context.IdRangeStart}, {context.IdRangeEnd}) and cannot be rebound to [{idRangeStart}, {idRangeEnd}).",
                nameof(contextKey));
        }

        // Stamp capture is context-wide state (captures are keyed on the context, signals stamp
        // with its epoch), so unlike the per-factory strictness a conflicting setting cannot be
        // honored -- half-stamped evidence would be worse than either choice.
        if (context.StampsEnabled != stampsEnabled)
        {
            throw new ArgumentException(
                $"Context key '{contextKey}' is already bound with causality stamps {(context.StampsEnabled ? "enabled" : "disabled")} and cannot be rebound with them {(stampsEnabled ? "enabled" : "disabled")}.",
                nameof(contextKey));
        }

        return context;
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
        return new(value, Context, Options.HasFlag(MemoFactoryOptions.ValidateWrittenValues))
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
        return new(value, Context, Options.HasFlag(MemoFactoryOptions.ValidateWrittenValues))
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
        return new(value, Context, Options.HasFlag(MemoFactoryOptions.ValidateWrittenValues));
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
        // Strict is the DEFAULT (issue #145 part A4, the Swift 6 language-mode analog);
        // DisableSendableChecks is the migration escape hatch.
        if (!Options.HasFlag(MemoFactoryOptions.DisableSendableChecks))
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
