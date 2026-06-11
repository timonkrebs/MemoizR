namespace MemoizR;

public sealed class MemoFactory
{

    private static Lock contextsLock = new();
    internal static Dictionary<string, WeakReference<Context>> CONTEXTS = new Dictionary<string, WeakReference<Context>>();
    internal Context Context { get; }
    public Lock Lock { get; } = new();

    // The SynchronizationContext reactions built from this factory marshal to (set via
    // MemoizR.Reactive's AddSynchronizationContext, or MemoizR.Wpf's AddWpfDispatcher): a
    // Reaction posts only its action with the already-evaluated dependency values, an
    // AdvancedReaction its whole Execute. Lives on the factory itself so the association is
    // discoverable and dies with the factory -- it previously sat in a static side-table in
    // another assembly, which rooted every registered factory forever.
    internal SynchronizationContext? SynchronizationContext { get; set; }

    public MemoFactory(string? contextKey = null) : this(contextKey, 1, int.MaxValue)
    {
    }

    // Pins this factory's context to the node-id slice [idRangeStart, idRangeEnd): distributed
    // peers use disjoint slices so causality stamps merged across peers can never collide on an
    // id, and a contiguous slice keeps merged stamps compact (see
    // docs/architecture/causality-trigger-clock.md). Rebinding an existing context key to a
    // different slice is a configuration conflict and throws.
    public MemoFactory(string? contextKey, int idRangeStart, int idRangeEnd = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(idRangeStart);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idRangeEnd, idRangeStart);

        // Default context is mapped to empty string
        if (string.IsNullOrWhiteSpace(contextKey))
        {
            Context = new(idRangeStart, idRangeEnd);
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
                    if (context.IdRangeStart != idRangeStart || context.IdRangeEnd != idRangeEnd)
                    {
                        throw new ArgumentException(
                            $"Context key '{contextKey}' is already bound to the node-id slice [{context.IdRangeStart}, {context.IdRangeEnd}) and cannot be rebound to [{idRangeStart}, {idRangeEnd}).",
                            nameof(contextKey));
                    }
                    Context = context;
                }
                else
                {
                    Context = new(idRangeStart, idRangeEnd);
                    weakContext.SetTarget(Context);
                }
            }
            else
            {
                Context = new(idRangeStart, idRangeEnd);
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
        return new(value, Context)
        {
            Label = label
        };
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