namespace MemoizR;

internal enum CacheState
{
    CacheClean = 0,
    CacheCheck = 1,
    CacheDirty = 2
}

internal interface IMemoizR : IMemoHandlR
{
    void UpdateIfNecessary();

    CacheState State { get; set; }

    void Stale(CacheState state);
}

internal interface IMemoHandlR
{
    IMemoizR[] Observers { get; set; }
}

public abstract class MemoHandlR<T> : IMemoHandlR
{
    internal IMemoHandlR[] sources = Array.Empty<IMemoHandlR>(); // sources in reference order, not deduplicated (up links)
    internal IMemoizR[] Observers { get; set; } = Array.Empty<IMemoizR>(); // nodes that have us as sources (down links)

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