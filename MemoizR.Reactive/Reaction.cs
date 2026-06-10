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

        // Eager-run contract: a reaction executes once on creation (SolidJS-style effect
        // semantics), scheduled through the same invalidation/debounce machinery as every other
        // trigger. TimeSpan.Zero deliberately bypasses the configured DebounceTime -- the initial
        // run should not wait out a debounce window meant for write coalescing. Note this is a
        // fire-and-forget background start from a constructor: ReactionBuilder's object
        // initializer (Label, DebounceTime) races it, which is benign only because the initial
        // run does not read either.
        Stale(CacheState.CacheDirty, TimeSpan.Zero);
    }

    protected override async Task Execute()
    {
        await action();
    }
}

