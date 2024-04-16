namespace MemoizR.Reactive;

public sealed class Reaction : ReactionBase
{
    private readonly Func<Task> action;

    internal Reaction(Func<Task> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute()
    {
        await action();
    }
}

