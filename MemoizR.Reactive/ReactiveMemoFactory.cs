namespace MemoizR.Reactive
{
    using MemoizR;

    public class ReactiveMemoFactory : MemoFactory
    {
        public ReactiveMemoFactory(string? contextKey = null) : base(contextKey) { }

        public Reaction CreateReaction(Func<Task> fn)
        {
            return new Reaction(fn, context);
        }

            public Reaction CreateReaction(string label, Func<Task> fn)
        {
            return new Reaction(fn, context, label);
        }
    }
}