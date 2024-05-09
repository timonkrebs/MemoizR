using System.Collections.Concurrent;
using System.Diagnostics;
using MemoizR.StructuredAsyncLock.Nito;

namespace MemoizR.StructuredAsyncLock;

/// <summary>
/// A collection of cancelable <see cref="TaskCompletionSource{T}"/> instances. Implementations must assume the caller is holding a lock.
/// </summary>
/// <typeparam name="T">The type of the results. If this isn't needed, use <see cref="Object"/>.</typeparam>
internal interface IAsyncWaitDictionary<T>
{
    /// <summary>
    /// Gets whether the dictionary is empty.
    /// </summary>
    bool IsEmpty { get; }

    /// <summary>
    /// Creates a new entry and queues it to this wait dictionary. The returned task must support both synchronous and asynchronous waits.
    /// </summary>
    /// <returns>The queued task.</returns>
    Task<T> Enqueue(double lockScope);

    /// <summary>
    /// Removes a single entry in the wait dictionary and completes it. This method may only be called if <see cref="IsEmpty"/> is <c>false</c>. The task continuations for the completed task must be executed asynchronously.
    /// </summary>
    /// <param name="result">The result used to complete the wait dictionary entry. If this isn't needed, use <c>default(T)</c>.</param>
    double Dequeue(T result, double? lockScope);
}

/// <summary>
/// The default wait dictionary implementation, which uses a dictionary-stack.
/// </summary>
/// <typeparam name="T">The type of the results. If this isn't needed, use <see cref="Object"/>.</typeparam>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(DefaultAsyncWaitDictionary<>.DebugView))]
internal sealed class DefaultAsyncWaitDictionary<T> : IAsyncWaitDictionary<T>
{
    private readonly ConcurrentDictionary<double, ConcurrentStack<TaskCompletionSource<T>>> _dictionary = new();

    private int Count
    {
        get { return _dictionary.Count; }
    }

    bool IAsyncWaitDictionary<T>.IsEmpty
    {
        get { return Count == 0; }
    }

    Task<T> IAsyncWaitDictionary<T>.Enqueue(double lockScope)
    {
        var tcs = TaskCompletionSourceExtensions.CreateAsyncTaskSource<T>();
        if (_dictionary.TryGetValue(lockScope, out var stack))
        {
            stack.Push(tcs);
            return tcs.Task;
        }
        var concurrentStack = new ConcurrentStack<TaskCompletionSource<T>>();
        concurrentStack.Push(tcs);

        _dictionary.TryAdd(lockScope, concurrentStack);
        return tcs.Task;
    }

    double IAsyncWaitDictionary<T>.Dequeue(T result, double? lockScope)
    {
        if (lockScope.HasValue && _dictionary.TryGetValue(lockScope!.Value, out var item))
        {
            HandleDequeue(item, result, lockScope.Value);
            return lockScope.Value;
        }

        var randomItem = _dictionary.Last();
        HandleDequeue(randomItem.Value, result, randomItem.Key);
        return randomItem.Key;
    }

    private void HandleDequeue(ConcurrentStack<TaskCompletionSource<T>> item, T result, double lockScope)
    {
        var c = item.Count;

        if (c == 0)
        {
            throw new InvalidOperationException("should not dequeue ");
        }

        if (c == 1)
        {
            item.TryPop(out var tcs);
            tcs!.TrySetResult(result);
            _dictionary.Remove(lockScope, out _);
        }
        else
        {
            item.TryPop(out _);
        }
    }

    [DebuggerNonUserCode]
    internal sealed class DebugView
    {
        private readonly DefaultAsyncWaitDictionary<T> _dictionary;

        public DebugView(DefaultAsyncWaitDictionary<T> dictionary)
        {
            _dictionary = dictionary;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public Task<T>[] Tasks
        {
            get
            {
                var result = new List<Task<T>>(_dictionary._dictionary.Count);
                foreach (var entry in _dictionary._dictionary)
                    result.Add(entry.Value.First().Task);
                return result.ToArray();
            }
        }
    }
}
