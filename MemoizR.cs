internal enum CacheState
{
    CacheClean,
    CacheCheck,
    CacheDirty
}

public class MemoizR<T>
{
    private T value;
    private Func<T> fn;
    private MemoizR<object>[] observers; // nodes that have us as sources (down links)
    private MemoizR<object>[] sources; // sources in reference order, not deduplicated (up links)

    private CacheState state;

    private string label;

    // private bool effect;
    // cleanups: ((oldValue: T) => void)[] = [];
    // equals = defaultEquality;

    MemoizR()
    {

    }
}