namespace MemoizR.Reactive
{
    using MemoizR;

    public class ReactiveMemoFactory : MemoFactory
    {
        private readonly TaskScheduler? sheduler;

        public ReactiveMemoFactory(string? contextKey = null) : base(contextKey) { }


        /// <summary>
        /// Initializes ReactiveMemoFactory with specific TaskScheduler.
        /// </summary>
        /// <param name="sheduler">The sheduler used to run the reaction on. This may not be <c>null</c>.</param>
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
}