namespace MemoizR;

public abstract class MemoHandlR<T> : IMemoHandlR
{
    internal IMemoHandlR[] Sources { get; set;} = Array.Empty<IMemoHandlR>(); // sources in reference order, not deduplicated (up links)
    internal IMemoizR[] Observers { get; set; } = Array.Empty<IMemoizR>(); // nodes that have us as sources (down links)

    IMemoHandlR[] IMemoHandlR.Sources { get => Sources; set => Sources = value; }
    IMemoizR[] IMemoHandlR.Observers { get => Observers; set => Observers = value; }

    protected Func<T?, T?, bool> equals;
    protected T? value = default;
    protected string? label;

    protected Context context;

    internal MemoHandlR(Context context, Func<T?, T?, bool>? equals)
    {
        this.context = context;
        this.equals = equals ?? ((a, b) => Object.Equals(a, b));
    }
}
