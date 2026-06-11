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
        // The eager initial run is NOT started here: the builder calls ScheduleInitialRun()
        // after the object initializer has assigned Label/DebounceTime (see ReactionBase).
    }

    protected override async Task Execute()
    {
        await action();
    }
}

