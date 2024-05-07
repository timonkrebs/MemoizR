using System.Collections.Concurrent;
using System.Diagnostics;

namespace MemoizR.StructuredAsyncLock.Nito;

/// <summary>
/// A collection of cancelable <see cref="TaskCompletionSource{T}"/> instances. Implementations must assume the caller is holding a lock.
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
    Task<T> Enqueue(double lockScope);

    /// <summary>
    /// Removes a single entry in the wait queue and completes it. This method may only be called if <see cref="IsEmpty"/> is <c>false</c>. The task continuations for the completed task must be executed asynchronously.
    /// </summary>
    /// <param name="result">The result used to complete the wait queue entry. If this isn't needed, use <c>default(T)</c>.</param>
    double Dequeue(T result, double? lockScope = null);

    /// <summary>
    /// Attempts to remove an entry from the wait queue and cancels it. The task continuations for the completed task must be executed asynchronously.
    /// </summary>
    /// <param name="task">The task to cancel.</param>
    /// <param name="cancellationToken">The cancellation token to use to cancel the task.</param>
    bool TryCancel(Task task, CancellationToken cancellationToken);
}

/// <summary>
/// Provides extension methods for wait queues.
/// </summary>
internal static class AsyncWaitQueueExtensions
{
    /// <summary>
    /// Creates a new entry and queues it to this wait queue. If the cancellation token is already canceled, this method immediately returns a canceled task without modifying the wait queue.
    /// </summary>
    /// <param name="this">The wait queue.</param>
    /// <param name="mutex">A synchronization object taken while cancelling the entry.</param>
    /// <param name="token">The token used to cancel the wait.</param>
    /// <returns>The queued task.</returns>
    public static Task<T> Enqueue<T>(this IAsyncWaitQueue<T> @this, object mutex, CancellationToken token, double lockScope)
    {
        if (token.IsCancellationRequested)
            return Task.FromCanceled<T>(token);

        var ret = @this.Enqueue(lockScope);
        if (!token.CanBeCanceled)
            return ret;

        var registration = token.Register(() =>
        {
            lock (mutex)
                @this.TryCancel(ret, token);
        }, useSynchronizationContext: false);
        ret.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return ret;
    }
}

/// <summary>
/// The default wait queue implementation, which uses a double-ended queue.
/// </summary>
/// <typeparam name="T">The type of the results. If this isn't needed, use <see cref="Object"/>.</typeparam>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(DefaultAsyncWaitQueue<>.DebugView))]
internal sealed class DefaultAsyncWaitQueue<T> : IAsyncWaitQueue<T>
{
    private readonly ConcurrentDictionary<double, ConcurrentStack<TaskCompletionSource<T>>> _dictionary = new();

    private int Count
    {
        get { return _dictionary.Count; }
    }

    bool IAsyncWaitQueue<T>.IsEmpty
    {
        get { return Count == 0; }
    }

    Task<T> IAsyncWaitQueue<T>.Enqueue(double lockScope)
    {
        var tcs = TaskCompletionSourceExtensions.CreateAsyncTaskSource<T>();
        if (_dictionary.TryGetValue(lockScope, out var stack))
        {
            stack.Push(tcs);
        }
        var concurrentStack = new ConcurrentStack<TaskCompletionSource<T>>();
        concurrentStack.Push(tcs);

        _dictionary.TryAdd(lockScope, concurrentStack);
        return tcs.Task;
    }

    double IAsyncWaitQueue<T>.Dequeue(T result, double? lockScope)
    {
        Console.WriteLine("Dequeue " + lockScope);
        if (lockScope.HasValue && _dictionary.TryGetValue(lockScope!.Value, out var item))
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
                _dictionary.Remove(lockScope.Value, out _);
            }
            else
            {
                item.TryPop(out _);
            }
            return lockScope.Value;
        }

        var randomItem = _dictionary.First();

        Console.WriteLine("randomItem " + randomItem.Key);

        var count = randomItem.Value.Count;

        if (count == 0)
        {
            throw new InvalidOperationException("should not dequeue ");
        }

        if (count == 1)
        {
            randomItem.Value.TryPop(out var tcs);
            tcs!.TrySetResult(result);
            _dictionary.Remove(randomItem.Key, out _);
        }
        else
        {
            randomItem.Value.TryPop(out _);
        }

        return randomItem.Key;
    }

    bool IAsyncWaitQueue<T>.TryCancel(Task task, CancellationToken cancellationToken)
    {
        Console.WriteLine("TryCancel");
        for (int i = 0; i != _dictionary.Count; ++i)
        {
            var element = _dictionary.ElementAt(i);
            if (element.Value.First().Task == task)
            {
                element.Value.First().TrySetCanceled(cancellationToken);
                _dictionary.Remove(element.Key, out _);
                return true;
            }
        }
        return false;
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
                var result = new List<Task<T>>(_queue._dictionary.Count);
                foreach (var entry in _queue._dictionary)
                    result.Add(entry.Value.First().Task);
                return result.ToArray();
            }
        }
    }
}
