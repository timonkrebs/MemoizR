namespace MemoizR;

public sealed class MemoFactory
{

    private static Lock contextsLock = new();
    internal static Dictionary<string, WeakReference<Context>> CONTEXTS = new Dictionary<string, WeakReference<Context>>();
    internal Context Context { get; }
    public Lock Lock { get; } = new();

    public MemoFactory(string? contextKey = null)
    {
        lock (contextsLock)
        {
            // Default context is mapped to empty string
            if (string.IsNullOrWhiteSpace(contextKey))
            {
                Context = new();
                return;
            }

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

            if (Context == null)
            {
                throw new NullReferenceException("Context can not be null");
            }
        }
    }

    public static void CleanUpContexts()
    {
        lock (contextsLock)
        {
            var keysToRemove = new List<string>();

            foreach (var kvp in CONTEXTS)
            {
                if (!kvp.Value.TryGetTarget(out _))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

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
}