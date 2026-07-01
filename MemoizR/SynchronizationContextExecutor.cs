namespace MemoizR;

/// <summary>
/// Adapts a <see cref="SynchronizationContext"/> (a UI thread's, typically) as an
/// <see cref="IExecutor"/>. This is what <c>AddSynchronizationContext</c> wraps the supplied
/// context in, so the reaction pipeline deals with one executor concept only.
/// </summary>
/// <remarks>
/// <see cref="IsCurrent"/> compares <see cref="SynchronizationContext.Current"/> to the wrapped
/// instance by reference: exact for contexts that install themselves while running callbacks
/// (the UI contexts do), best-effort false for contexts that post to foreign threads without
/// setting Current -- a missed assertion there reports not-isolated, which is the safe direction.
/// </remarks>
public sealed class SynchronizationContextExecutor(SynchronizationContext synchronizationContext) : IExecutor
{
    public void Enqueue(Action work)
    {
        synchronizationContext.Post(static state => ((Action)state!)(), work);
    }

    public bool IsCurrent => ReferenceEquals(SynchronizationContext.Current, synchronizationContext);
}
