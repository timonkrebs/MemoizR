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

    /// <summary>
    /// Opens a transition scope (ADR 0007): Sets performed inside it are tagged, and the
    /// returned <see cref="Transition"/> tracks every reaction the writes invalidate until all
    /// of them have committed clean again -- observable via <see cref="Transition.IsPending"/> /
    /// <see cref="Transition.Pending"/> and awaitable via <see cref="Transition.Settled"/>.
    /// <c>using</c> seals the wavefront at scope end; <c>await using</c> additionally awaits
    /// settlement. Scopes nest innermost-wins: writes inside an inner scope are tracked by the
    /// inner transition only.
    /// </summary>
    public static Transition BeginTransition(this MemoFactory memoFactory)
    {
        return new(memoFactory.Context);
    }

    /// <summary>
    /// Creates an optimistic view over <paramref name="source"/> (ADR 0007): reads return the
    /// source value with every in-flight optimistic patch applied on top. Pair with
    /// <see cref="CreateAction"/> -- patches are applied through an action run and dropped
    /// automatically when it ends, giving instant projection with structural rollback.
    /// </summary>
    public static OptimisticState<T> CreateOptimistic<T>(this MemoFactory memoFactory, IStateGetR<T> source, string label = "Optimistic")
    {
        // The composed view's CreateMemoizR checks T too; the explicit gate here makes the
        // strict contract visible at the API boundary (and survives view-construction
        // refactors) -- the source need not come from a strict MemoizR creation at all.
        memoFactory.EnsureSendableIfStrict<T>();
        return new(memoFactory, source, label);
    }

    /// <summary>
    /// Creates a reusable process-layer action (ADR 0007): the body projects optimistic
    /// patches via the context, runs the real process on the context's token, and writes the
    /// confirmed result to the source of truth. A faulted or cancelled run rolls back
    /// automatically (its patches are dropped, the source was never touched); every run's
    /// effect wavefront is tracked by its own <see cref="Transition"/>.
    /// </summary>
    public static ReactiveAction<TPayload> CreateAction<TPayload>(this MemoFactory memoFactory, Func<TPayload, OptimisticActionContext, Task> body, string label = "Action")
    {
        // The payload crosses flows (Run captures it onto a detached body task), so strict mode
        // holds it to the same Sendable bar as every other cross-flow value.
        memoFactory.EnsureSendableIfStrict<TPayload>();
        return new(memoFactory.Context, body, label);
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
    }
}
