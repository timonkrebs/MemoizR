namespace MemoizR.Reactive;

public sealed class AdvancedReaction : ReactionBase
{
    private readonly Func<CancellationTokenSource, Task> fn;

    internal AdvancedReaction(Func<CancellationTokenSource, Task> fn,
    Context context,
    SynchronizationContext? synchronizationContext = null,
    string label = "Label")
    : base(context, synchronizationContext, label)
    {
        this.fn = fn;

        Task.Run(Init).GetAwaiter().GetResult();
    }

    protected override Task Execute(CancellationTokenSource cts)
    {
        return fn(cts);
    }
}
