
using System.Numerics;
using MemoizR.StructuredConcurrency;

namespace MemoizR.Reactive;
public static class StructuredConcurrencyFactory
{
    private static Dictionary<MemoFactory, CancellationTokenSource> cancellationTokenSources = new Dictionary<MemoFactory, CancellationTokenSource>();

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, params Func<CancellationToken, Task<T>>[] fns) where T : INumber<T>
    {
        lock (memoFactory)
        {
            if (cancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.context, cancellationTokenSource);
            }

            var cts = new CancellationTokenSource();
            cancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.context, cts);
        }
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, Func<T, T?, T?> reduce, params Func<CancellationToken, Task<T>>[] fns)
    {
        lock (memoFactory)
        {
            if (cancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.context, cancellationTokenSource);
            }

            var cts = new CancellationTokenSource();
            cancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.context, cts);
        }
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, string label, params Func<CancellationToken, Task<T>>[] fns) where T : INumber<T>
    {
        lock (memoFactory)
        {
            if (cancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.context, cancellationTokenSource, label);
            }

            var cts = new CancellationTokenSource();
            cancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.context, cts, label);
        }
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, string label, Func<T, T?, T?> reduce, params Func<CancellationToken, Task<T>>[] fns)
    {
        lock (memoFactory)
        {
            if (cancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.context, cancellationTokenSource, label);
            }

            var cts = new CancellationTokenSource();
            cancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.context, cts, label);
        }
    }
}