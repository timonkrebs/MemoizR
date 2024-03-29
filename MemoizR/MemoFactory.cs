namespace MemoizR;

public sealed class MemoFactory
{
    internal static Dictionary<string, WeakReference<Context>> CONTEXTS = new Dictionary<string, WeakReference<Context>>();
    internal Context Context { get; }

    public MemoFactory(string? contextKey = null)
    {
        lock (CONTEXTS)
        {
            // Default context is mapped to empty string
            if (string.IsNullOrWhiteSpace(contextKey))
            {
                contextKey = "";
            }

            if (CONTEXTS.TryGetValue(contextKey, out var weakContext))
            {
                if (weakContext.TryGetTarget(out var context))
                {
                    Context = context;
                }
                else
                {
                    Context = new Context();
                    weakContext.SetTarget(Context);
                }
            }
            else
            {
                Context = new Context();
                CONTEXTS.Add(contextKey, new WeakReference<Context>(Context));
            }

            if (Context == null)
            {
                throw new NullReferenceException("Context can not be null");
            }
        }
    }

    public static void CleanUpContexts()
    {
        lock (CONTEXTS)
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
        return CreateMemoizR("MemoizR", fn);
    }

    public MemoizR<T> CreateMemoizR<T>(string label, Func<Task<T>> fn, Func<T?, T?, bool>? equals = null)
    {
        return new MemoizR<T>(fn, Context, equals)
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
        return new Signal<T>(value, Context)
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
        return new EagerRelativeSignal<T>(value, Context)
        {
            Label = label
        };
    }
}