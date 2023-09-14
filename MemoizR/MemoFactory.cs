using MemoizR;

public class MemoFactory
{
    private static Dictionary<string, WeakReference<Context>> contexts = new Dictionary<string, WeakReference<Context>>();
    private Context context;

    public MemoFactory(string? contextKey = null)
    {
        // Default context is mapped to empty string
        if (string.IsNullOrWhiteSpace(contextKey))
        {
            contextKey = "";
        }

        if (contexts.TryGetValue(contextKey, out var weakContext))
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
            return;
        }

        this.context = new Context();
        contexts.Add(contextKey, new WeakReference<Context>(this.context));
    }

    public MemoizR<T> CreateMemoizR<T>(Func<T> fn, string label = "Label", Func<T?, T?, bool>? equals = null)
    {
        return new MemoizR<T>(fn, context, label, equals);
    }

    public MemoSetR<T> CreateMemoSetR<T>(T value, string label = "Label", Func<T?, T?, bool>? equals = null)
    {
        return new MemoSetR<T>(value, context, label, equals);
    }
}