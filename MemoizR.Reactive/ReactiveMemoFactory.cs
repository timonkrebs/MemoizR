using MemoizR;

namespace MemoizR.Reactive;

public class ReactiveMemoFactory : MemoFactory
{
    private readonly TaskScheduler? sheduler;

    // Constructor for initializing the ReactiveMemoFactory without a specific TaskScheduler.
    public ReactiveMemoFactory(string? contextKey = null) : base(contextKey) { }

    /// <summary>
    /// Constructor for initializing the ReactiveMemoFactory with a specific TaskScheduler.
    /// </summary>
    /// <param name="sheduler">The TaskScheduler used to run the reaction on. Must not be <c>null</c>.</param>
    public ReactiveMemoFactory(TaskScheduler sheduler, string? contextKey = null) : base(contextKey)
    {
        this.sheduler = sheduler;
    }

    public Reaction CreateReaction(Func<Task> fn)
    {
        return new Reaction(fn, context, sheduler);
    }

    public Reaction CreateReaction(string label, Func<Task> fn)
    {
        return new Reaction(fn, context, sheduler, label);
    }
}
