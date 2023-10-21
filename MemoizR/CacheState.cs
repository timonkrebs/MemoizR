namespace MemoizR;

internal enum CacheState
{
    Evaluating = -1,
    CacheClean = 0,
    CacheCheck = 1,
    CacheDirty = 2
}
