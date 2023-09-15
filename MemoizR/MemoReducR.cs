namespace MemoizR;

public sealed class MemoReducR<T> : MemoHandlR<T> // ToDo: , IMemoizR
{
    internal MemoReducR(Func<T, T> reduce, Context context, string label = "Label") : base(context, null)
    {
        this.label = label;
    }

    public T? Get()
    {
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
            
        }

        return value;
    }
}
