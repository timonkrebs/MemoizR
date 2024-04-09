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

        Task.Run(Init).GetAwaiter().GetResult();
    }

    protected override Task Execute()
    {
        return fn();
    }
}
