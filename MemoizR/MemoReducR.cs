namespace MemoizR;

public sealed class MemoReducR<T> : MemoHandlR<T> // ToDo: , IMemoizR
{
    private List<Func<T, T>> reducers = new List<Func<T, T>>();


    internal MemoReducR(Context context, string label = "Label") : base(context, null)
    {
        this.label = label;
    }

    public void Set(Func<T, T> reduce)
    {
        lock (context)
        {
            for (int i = 0; i < Observers.Length; i++)
            {
                var observer = Observers[i];
                observer.Stale(CacheState.CacheDirty);
            }

            reducers.Add(reduce);
        }
    }

    public T? Get()
    {
        if (!reducers.Any())
        {
            return value;
        }


        if (context.CurrentReaction == null) // ToDo: State == CacheState.CacheClean
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

            foreach (var reducer in reducers)
            {
                value = reducer(value!);
            }
            reducers.Clear();
        }

        return value;
    }
}
