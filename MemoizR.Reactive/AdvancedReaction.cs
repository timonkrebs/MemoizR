namespace MemoizR.Reactive;

public sealed class AdvancedReaction : ReactionBase
{
    private readonly Func<Task> fn;

    internal AdvancedReaction(Func<Task> fn,
    Context context,
    IExecutor? executor = null,
    TimeProvider? timeProvider = null)
    : base(context, executor, timeProvider)
    {
        this.fn = fn;
        // The eager initial run is NOT started here: the builder calls ScheduleInitialRun()
        // after the object initializer has assigned Label/DebounceTime (see ReactionBase).
    }

    protected override Task Execute()
    {
        return fn();
    }
}
