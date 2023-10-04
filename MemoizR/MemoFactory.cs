using MemoizR;

public class MemoFactory
{
    internal static Dictionary<string, WeakReference<Context>> CONTEXTS = new Dictionary<string, WeakReference<Context>>();
    internal Context context;

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
                if (weakContext.TryGetTarget(out var context) && context != null)
                {
                    this.context = context;
                }
                else
                {
                    this.context = new Context();
                    weakContext.SetTarget(this.context);
                }
            }
            else
            {
                this.context = new Context();
                CONTEXTS.Add(contextKey, new WeakReference<Context>(this.context));
            }

            if (this.context == null)
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
        return new MemoizR<T>(fn, context);
    }

    public MemoizR<T> CreateMemoizR<T>(string label, Func<Task<T>> fn, Func<T?, T?, bool>? equals = null)
    {
        return new MemoizR<T>(fn, context, label, equals);
    }

    public Signal<T> CreateSignal<T>(T value)
    {
        return new Signal<T>(value, context);
    }

    public Signal<T> CreateSignal<T>(string label, T value)
    {
        return new Signal<T>(value, context, label);
    }

    public EagerRelativeSignal<T> CreateEagerRelativeSignal<T>(T value)
    {
        return new EagerRelativeSignal<T>(value, context);
    }

    public EagerRelativeSignal<T> CreateEagerRelativeSignal<T>(string label, T value)
    {
        return new EagerRelativeSignal<T>(value, context, label);
    }
}