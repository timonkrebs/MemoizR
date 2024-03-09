namespace MemoizR;

internal interface IMemoizR : IMemoHandlR
{
    Task UpdateIfNecessary();

    CacheState State { get; set; }

    ValueTask Stale(CacheState state);
}
