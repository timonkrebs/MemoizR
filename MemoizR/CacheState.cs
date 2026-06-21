namespace MemoizR;

/// <summary>
/// Tracks the validity of a cached node's computed value within the reactive graph.
/// </summary>
internal enum CacheState
{
    /// <summary>The node is currently being evaluated (used for cycle detection).</summary>
    Evaluating = -1,
    /// <summary>The cached value is up-to-date; no recomputation needed.</summary>
    CacheClean = 0,
    /// <summary>A parent may have changed; check parents before deciding whether to recompute.</summary>
    CacheCheck = 1,
    /// <summary>The cached value is stale; recomputation is required.</summary>
    CacheDirty = 2
}
