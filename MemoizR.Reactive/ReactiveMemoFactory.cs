using MemoizR.Reactive;

namespace MemoizR;

public static class ReactiveMemoFactory
{
    /// <summary>
    /// Pins the side effects of reactions built from this factory to an executor (a UI thread's
    /// SynchronizationContext wrapped in a <see cref="SynchronizationContextExecutor"/>, a
    /// <see cref="DedicatedThreadExecutor"/>, or a custom <see cref="IExecutor"/>) -- the custom
    /// actor executor analog (SE-0392, issue #36). Applies to reactions built AFTER the call.
    /// </summary>
    public static MemoFactory AddExecutor(this MemoFactory memoFactory, IExecutor executor)
    {
        memoFactory.Executor = executor;
        return memoFactory;
    }

    public static MemoFactory AddSynchronizationContext(this MemoFactory memoFactory, SynchronizationContext synchronizationContext)
    {
        memoFactory.Executor = new SynchronizationContextExecutor(synchronizationContext);
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
        return new(memoFactory, memoFactory.Executor, label);
    }

    // Factory-level sugar for the common case: identical to BuildReaction().CreateReaction(..)
    // with the default label and debounce -- use BuildReaction to configure either. The
    // threading contract is the builder's: dependencies are registered in parameter order, the
    // values are computed in parallel on the thread pool, and only the action is marshalled to
    // the factory's SynchronizationContext when one is registered (e.g. MemoizR.Wpf).

    public static Reaction CreateReaction<T>(this MemoFactory memoFactory, IStateGetR<T> memo, Action<T> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo, action);
    }

    public static Reaction CreateReaction<T1, T2>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, Action<T1, T2> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, action);
    }

    public static Reaction CreateReaction<T1, T2, T3>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, Action<T1, T2, T3> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, Action<T1, T2, T3, T4> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, Action<T1, T2, T3, T4, T5> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, Action<T1, T2, T3, T4, T5, T6> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, Action<T1, T2, T3, T4, T5, T6, T7> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, Action<T1, T2, T3, T4, T5, T6, T7, T8> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, memo11, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, memo11, memo12, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, memo11, memo12, memo13, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, memo11, memo12, memo13, memo14, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, memo11, memo12, memo13, memo14, memo15, action);
    }

    public static Reaction CreateReaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(this MemoFactory memoFactory, IStateGetR<T1> memo1, IStateGetR<T2> memo2, IStateGetR<T3> memo3, IStateGetR<T4> memo4, IStateGetR<T5> memo5, IStateGetR<T6> memo6, IStateGetR<T7> memo7, IStateGetR<T8> memo8, IStateGetR<T9> memo9, IStateGetR<T10> memo10, IStateGetR<T11> memo11, IStateGetR<T12> memo12, IStateGetR<T13> memo13, IStateGetR<T14> memo14, IStateGetR<T15> memo15, IStateGetR<T16> memo16, Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> action)
    {
        return memoFactory.BuildReaction().CreateReaction(memo1, memo2, memo3, memo4, memo5, memo6, memo7, memo8, memo9, memo10, memo11, memo12, memo13, memo14, memo15, memo16, action);
    }}
