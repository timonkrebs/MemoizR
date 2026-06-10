using System.Diagnostics;
using Nito.Collections;

namespace MemoizR.StructuredAsyncLock.Nito;

/// <summary>
/// A collection of <see cref="TaskCompletionSource{T}"/> instances. Implementations must assume the caller is holding a lock.
/// </summary>
/// <typeparam name="T">The type of the results. If this isn't needed, use <see cref="Object"/>.</typeparam>
internal interface IAsyncWaitQueue<T>
{
    /// <summary>
    /// Gets whether the queue is empty.
    /// </summary>
    bool IsEmpty { get; }

    /// <summary>
    /// Creates a new entry and queues it to this wait queue. The returned task must support both synchronous and asynchronous waits.
    /// </summary>
    /// <returns>The queued task.</returns>
    Task<T> Enqueue(Guid lockScope);

    /// <summary>
    /// Removes a single entry in the wait queue and completes it. This method may only be called if <see cref="IsEmpty"/> is <c>false</c>. The task continuations for the completed task must be executed asynchronously.
    /// </summary>
    /// <param name="result">The result used to complete the wait queue entry. If this isn't needed, use <c>default(T)</c>.</param>
    /// <returns>The lock scope associated with the completed entry.</returns>
    Guid Dequeue(T? result = default);
}

/// <summary>
/// The default wait queue implementation, which uses a double-ended queue.
/// </summary>
/// <typeparam name="T">The type of the results. If this isn't needed, use <see cref="Object"/>.</typeparam>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(DefaultAsyncWaitQueue<>.DebugView))]
internal sealed class DefaultAsyncWaitQueue<T> : IAsyncWaitQueue<T>
{
    private readonly Deque<(TaskCompletionSource<T>, Guid)> _queue = new();

    private int Count
    {
        get { return _queue.Count; }
    }

    bool IAsyncWaitQueue<T>.IsEmpty
    {
        get { return Count == 0; }
    }

    Task<T> IAsyncWaitQueue<T>.Enqueue(Guid lockScope)
    {
        var tcs = TaskCompletionSourceExtensions.CreateAsyncTaskSource<T>();
        _queue.AddToBack((tcs, lockScope));
        return tcs.Task;
    }

    Guid IAsyncWaitQueue<T>.Dequeue(T? result)
    {
        var res = _queue.RemoveFromFront();

        res.Item1.TrySetResult(result!);

        return res.Item2;
    }

    [DebuggerNonUserCode]
    internal sealed class DebugView
    {
        private readonly DefaultAsyncWaitQueue<T> _queue;

        public DebugView(DefaultAsyncWaitQueue<T> queue)
        {
            _queue = queue;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public Task<T>[] Tasks
        {
            get
            {
                var result = new List<Task<T>>(_queue._queue.Count);
                foreach (var entry in _queue._queue)
                    result.Add(entry.Item1.Task);
                return result.ToArray();
            }
        }
    }
}
