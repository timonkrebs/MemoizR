namespace MemoizR.Reactive;

public sealed class AdvancedReaction : ReactionBase
{
    private readonly Func<Task> fn;

    internal AdvancedReaction(Func<Task> fn,
    Context context,
    IExecutor? executor = null)
    : base(context, executor)
    {
        this.fn = fn;

        Stale(CacheState.CacheDirty, TimeSpan.Zero);
    }

    protected override Task Execute()
    {
        return fn();
    }
}
