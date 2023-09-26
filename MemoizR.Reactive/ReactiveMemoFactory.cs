namespace MemoizR.Reactive;
using MemoizR;

public class ReactiveMemoFactory : MemoFactory
{
    public ReactiveMemoFactory(string? contextKey = null) : base(contextKey) { }

    public Reaction CreateReaction(Action fn, string label = "Label")
    {
        return new Reaction(fn, context, label);
    }
}