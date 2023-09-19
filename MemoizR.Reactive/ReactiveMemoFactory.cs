namespace MemoizR.Reactive;
using MemoizR;

public class ReactiveMemoFactory : MemoFactory
{
    public ReactiveMemoFactory(string? contextKey = null) : base(contextKey) { }

    public Reaction<T> CreateReaction<T>(Func<T> fn, string label = "Label", Func<T?, T?, bool>? equals = null)
    {
        return new Reaction<T>(fn, context, label, equals);
    }

    public ReactionReducR<T> CreateReactionReducR<T>(Func<T?, T> fn, string label = "Label", Func<T?, T?, bool>? equals = null)
    {
        return new ReactionReducR<T>(fn, context, label, equals);
    }
}