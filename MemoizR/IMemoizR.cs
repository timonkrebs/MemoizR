namespace MemoizR;

internal interface IMemoizR : IMemoHandlR
{
    Task UpdateIfNecessary();

    CacheState State { get; set; }

    Task Stale(CacheState state);
}
