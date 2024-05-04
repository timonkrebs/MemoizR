namespace MemoizR.Reactive;

public sealed class AdvancedReaction : ReactionBase
{
    private readonly Func<Task> fn;

    internal AdvancedReaction(Func<Task> fn,
    Context context,
    SynchronizationContext? synchronizationContext = null)
    : base(context, synchronizationContext)
    {
        this.fn = fn;

        var debounceTime = DebounceTime;
        DebounceTime = TimeSpan.Zero;
        Stale(CacheState.CacheDirty);
        DebounceTime = debounceTime;
    }

    protected override Task Execute()
    {
        return fn();
    }
}
