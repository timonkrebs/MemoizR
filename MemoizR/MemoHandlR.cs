using Nito.AsyncEx;

namespace MemoizR;

public abstract class SignalHandlR : IMemoHandlR
{
    internal IMemoHandlR[] Sources { get; set; } = []; // sources in reference order, not deduplicated (up links)
    internal WeakReference<IMemoizR>[] Observers { get; set; } = []; // nodes that have us as sources (down links)

    internal Context Context;

    protected AsyncLock mutex = new AsyncLock();

    IMemoHandlR[] IMemoHandlR.Sources { get => Sources; set => Sources = value; }
    WeakReference<IMemoizR>[] IMemoHandlR.Observers { get => Observers; set => Observers = value; }

    public string Label { get; init; } = "Label";

    internal SignalHandlR(Context context)
    {
        this.Context = context;
    }
}

public abstract class MemoHandlR<T> : SignalHandlR
{
    protected Func<T?, T?, bool> equals;
    internal T Value = default!;

    internal MemoHandlR(Context context, Func<T?, T?, bool>? equals) : base(context)
    {
        this.equals = equals ?? ((a, b) => Object.Equals(a, b));
    }
}
