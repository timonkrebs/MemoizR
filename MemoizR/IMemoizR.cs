namespace MemoizR;

internal interface IMemoizR : IMemoHandlR
{
    void UpdateIfNecessary();

    CacheState State { get; set; }

    void Stale(CacheState state);
}
