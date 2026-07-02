namespace MemoizR;

/// <summary>
/// Represents a reactive state container that can be asynchronously read.
/// </summary>
/// <typeparam name="T">The type of the state value.</typeparam>
public interface IStateGetR<T>
{
    /// <summary>Gets the current value, recomputing if the cached value is stale.</summary>
    public Task<T> Get();
}
