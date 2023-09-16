namespace MemoizR;

public sealed class EagerRelativeSignal<T> : MemoHandlR<T>
{
    internal EagerRelativeSignal(T value, Context context, string label = "Label") : base(context, null)
    {
        this.value = value;
        this.label = label;
    }

    public void Set(Func<T?, T> fn)
    {
        lock (context)
        {
            for (int i = 0; i < Observers.Length; i++)
            {
                var observer = Observers[i];
                observer.Stale(CacheState.CacheDirty);
            }

            value = fn(value);
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
