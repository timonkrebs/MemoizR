namespace MemoizR;

public sealed class MemoFactory
{
    internal static Dictionary<string, WeakReference<Context>> CONTEXTS = new Dictionary<string, WeakReference<Context>>();
    internal Context Context;

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
                    this.Context = context;
                }
                else
                {
                    this.Context = new Context();
                    weakContext.SetTarget(this.Context);
                }
            }
            else
            {
                this.Context = new Context();
                CONTEXTS.Add(contextKey, new WeakReference<Context>(this.Context));
            }

            if (this.Context == null)
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
        return new MemoizR<T>(fn, Context);
    }

    public MemoizR<T> CreateMemoizR<T>(string label, Func<Task<T>> fn, Func<T?, T?, bool>? equals = null)
    {
        return new MemoizR<T>(fn, Context, label, equals);
    }

    public Signal<T> CreateSignal<T>(T value)
    {
        return new Signal<T>(value, Context);
    }

    public Signal<T> CreateSignal<T>(string label, T value)
    {
        return new Signal<T>(value, Context, label);
    }

    public EagerRelativeSignal<T> CreateEagerRelativeSignal<T>(T value)
    {
        return new EagerRelativeSignal<T>(value, Context);
    }

    public EagerRelativeSignal<T> CreateEagerRelativeSignal<T>(string label, T value)
    {
        return new EagerRelativeSignal<T>(value, Context, label);
    }
}