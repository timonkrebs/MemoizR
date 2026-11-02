namespace MemoizR.Reactive;

[Sendable] // internally synchronized by design: safe to share across flows (and to hold in statics, see MZR004)
public sealed class Reaction : ReactionBase
{
    private readonly Func<Task> action;

    // Deliberately no SynchronizationContext: a Reaction's marshalling is owned by its composed
    // body (ReactionBuilder evaluates the dependencies on the calling flow and posts only the
    // user action). Base-level whole-Execute posting would put graph evaluation back on the
    // context AND nest a second post around the action's own. AdvancedReaction is the type for
    // bodies that must run on the context as a whole.
    internal Reaction(Func<Task> action,
                      Context context,
                      TimeProvider? timeProvider = null)
        : base(context, timeProvider: timeProvider)
    {
        this.action = action;
        // The eager initial run is NOT started here: the builder calls ScheduleInitialRun()
        // after the object initializer has assigned Label/DebounceTime (see ReactionBase).
    }

    protected override bool ResumeOnDetachedScope => true;

    protected override Task Execute()
    {
        return action();
    }
}

