using Nito.AsyncEx;

namespace MemoizR;

public abstract class SignalHandlR : IMemoHandlR
{
    internal IMemoHandlR[] Sources { get; set; } = []; // sources in reference order, not deduplicated (up links)
    internal WeakReference<IMemoizR>[] Observers { get; set; } = []; // nodes that have us as sources (down links)

    internal Context Context;

    protected AsyncLock mutex = new();

    IMemoHandlR[] IMemoHandlR.Sources
    {
        get => Sources;
        set
        {
            lock (this)
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
            lock (this)
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
    internal T Value = default!;

    internal MemoHandlR(Context context) : base(context)
    {
    }
}
