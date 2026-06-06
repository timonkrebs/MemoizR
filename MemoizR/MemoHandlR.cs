using Nito.AsyncEx;

namespace MemoizR;

public abstract class SignalHandlR : IMemoHandlR
{
    private Lock Lock { get; } = new();
    internal IMemoHandlR[] Sources { get; set; } = []; // sources in reference order, not deduplicated (up links)
    internal WeakReference<IMemoizR>[] Observers { get; set; } = []; // nodes that have us as sources (down links)

    internal Context Context;

    protected AsyncLock mutex = new();

    IMemoHandlR[] IMemoHandlR.Sources
    {
        get => Sources;
        set
        {
            lock (Lock)
            {
                Sources = value;
            }
        }
    }
    WeakReference<IMemoizR>[] IMemoHandlR.Observers
    {
        get => Observers;
        set
        {
            lock (Lock)
            {
                Observers = value;
            }
        }
    }

    internal bool isStartingComponent;

    public string Label { get; init; } = "Label";

    internal SignalHandlR(Context context)
    {
        this.Context = context;
    }
}

public abstract class MemoHandlR<T> : SignalHandlR
{
    // Value is read on the lock-free Get fast path while another flow may be writing it under the
    // ContextLock. A generic T can be neither marked `volatile` nor read with Volatile.Read (the
    // generic overload is class-constrained), and a large struct T can tear under a concurrent
    // write. So the value is published through an immutable box held in a single volatile
    // reference: a write swaps in a fully-constructed box (an atomic reference store with release
    // semantics), and a read takes the reference once and returns its readonly field -- always a
    // complete, untorn value. Every Update writes Value before setting State = CacheClean (a
    // volatile release) and the fast path reads State (a volatile acquire) before Value, so a
    // reader that observes CacheClean is guaranteed to see the box of that-or-a-newer clean
    // generation. The read is therefore a linearizable snapshot, not an eventually-consistent one.
    private volatile ValueBox valueBox = new(default!);

    internal T Value
    {
        get => valueBox.Value;
        set => valueBox = new ValueBox(value);
    }

    internal MemoHandlR(Context context) : base(context)
    {
    }

    private sealed class ValueBox(T value)
    {
        public readonly T Value = value;
    }
}
