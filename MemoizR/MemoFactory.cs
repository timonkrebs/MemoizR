using MemoizR;

public class MemoFactory
{
    internal static Dictionary<string, WeakReference<Context>> CONTEXTS = new Dictionary<string, WeakReference<Context>>();
    internal Context context;

    public MemoFactory(string? contextKey = null)
    {
        // Default context is mapped to empty string
        if (string.IsNullOrWhiteSpace(contextKey))
        {
            contextKey = "";
        }

        lock (CONTEXTS)
        {
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
        }

        if (this.context == null)
        {
            throw new NullReferenceException("Context can not be null");
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

    public MemoizR<T> CreateMemoizR<T>(Func<T> fn, string label = "Label", Func<T?, T?, bool>? equals = null)
    {
        return new MemoizR<T>(fn, context, label, equals);
    }

    public Signal<T> CreateSignal<T>(T value, string label = "Label", Func<T?, T?, bool>? equals = null)
    {
        return new Signal<T>(value, context, label, equals);
    }

    public EagerRelativeSignal<T> CreateEagerRelativeSignal<T>(T value, string label = "Label")
    {
        return new EagerRelativeSignal<T>(value, context, label);
    }
}