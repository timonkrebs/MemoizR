using MemoizR;

namespace MemoizR.Reactive;

public class ReactiveMemoFactory : MemoFactory
{
    private readonly SynchronizationContext? synchronizationContext;

    // Constructor for initializing the ReactiveMemoFactory without a specific TaskScheduler.
    public ReactiveMemoFactory(string? contextKey = null) : base(contextKey) { }

    /// <summary>
    /// Constructor for initializing the ReactiveMemoFactory with a specific TaskScheduler.
    /// </summary>
    /// <param name="synchronizationContext">The TaskScheduler used to run the reaction on. Must not be <c>null</c>.</param>
    public ReactiveMemoFactory(SynchronizationContext synchronizationContext, string? contextKey = null) : base(contextKey)
    {
        this.synchronizationContext = synchronizationContext;
    }

    public Reaction CreateReaction(Func<Task> fn)
    {
        return new Reaction(fn, context, synchronizationContext);
    }

    public Reaction CreateReaction(string label, Func<Task> fn)
    {
        return new Reaction(fn, context, synchronizationContext, label);
    }
}
