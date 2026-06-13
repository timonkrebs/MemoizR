using MemoizR.Reactive;

namespace MemoizR;

public static partial class ReactiveMemoFactory
{
    public static MemoFactory AddSynchronizationContext(this MemoFactory memoFactory, SynchronizationContext synchronizationContext)
    {
        memoFactory.SynchronizationContext = synchronizationContext;
        return memoFactory;
    }

    // Like AddSynchronizationContext, the registration applies to reactions built afterwards:
    // each ReactionBuilder captures the provider at build time. Inject a fake provider (e.g.
    // Microsoft.Extensions.Time.Testing.FakeTimeProvider) to drive debounce windows from a test
    // instead of waiting out wall-clock time.
    public static MemoFactory AddTimeProvider(this MemoFactory memoFactory, TimeProvider timeProvider)
    {
        memoFactory.TimeProvider = timeProvider;
        return memoFactory;
    }

    public static ReactionBuilder BuildReaction(this MemoFactory memoFactory, string label = "Reaction")
    {
        return new(memoFactory, memoFactory.SynchronizationContext, label);
    }

    // Factory-level sugar for the common case: identical to BuildReaction().CreateReaction(..)
    // with the default label and debounce -- use BuildReaction to configure either. The
    // threading contract is the builder's: dependencies are registered in parameter order, the
    // values are computed in parallel on the thread pool, and only the action is marshalled to
    // the factory's SynchronizationContext when one is registered (e.g. MemoizR.Wpf). The
    // strongly-typed CreateReaction<T1, ..., Tn> arity overloads (n = 1..16) live in the
    // generated half of this partial class, emitted by MemoizR.Reactive.SourceGenerator.
}
