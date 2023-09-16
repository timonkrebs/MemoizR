namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>
{
    internal Signal(T value, Context context, string label = "Label", Func<T?, T?, bool>? equals = null) : base(context, equals)
    {
        this.value = value;
        this.label = label;
    }

    public void Set(T value)
    {
        if (equals(this.value, value))
        {
            return;
        }

        Interlocked.Increment(ref context.WaitCount);
        context.WaitHandle.Reset();

        for (int i = 0; i < Observers.Length; i++)
        {
            var observer = Observers[i];
            observer.Stale(CacheState.CacheDirty);
        }

        this.value = value;

        Interlocked.Decrement(ref context.WaitCount);
        
        if (Volatile.Read(ref context.WaitCount) == 0)
        {
            context.WaitHandle.Set();
        }
    }

    public T? Get()
    {
        if (context.CurrentReaction == null)
        {
            return value;
        }

        lock (context)
        {
            if ((context.CurrentGets == null || !(context.CurrentGets.Length > 0)) &&
              (context.CurrentReaction.sources != null && context.CurrentReaction.sources.Length > 0) &&
              context.CurrentReaction.sources[context.CurrentGetsIndex].Equals(this)
            )
            {
                context.CurrentGetsIndex++;
            }
            else
            {
                if (!context.CurrentGets!.Any()) context.CurrentGets = new[] { this };
                else context.CurrentGets = context.CurrentGets!.Union(new[] { this }).ToArray();
            }
        }

        return value;
    }
}