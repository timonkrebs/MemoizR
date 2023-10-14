using System.Numerics;
using MemoizR.StructuredConcurrency;

namespace MemoizR;
public static class StructuredConcurrencyFactory
{
    private static readonly Dictionary<MemoFactory, CancellationTokenSource> CancellationTokenSources = new ();

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, params Func<CancellationToken, Task<T>>[] fns) where T : INumber<T>
    {
        lock (memoFactory)
        {
            if (CancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.Context, cancellationTokenSource);
            }

            var cts = new CancellationTokenSource();
            CancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.Context, cts);
        }
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, Func<T, T?, T?> reduce, params Func<CancellationToken, Task<T>>[] fns)
    {
        lock (memoFactory)
        {
            if (CancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.Context, cancellationTokenSource);
            }

            var cts = new CancellationTokenSource();
            CancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.Context, cts);
        }
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, string label, params Func<CancellationToken, Task<T>>[] fns) where T : INumber<T>
    {
        lock (memoFactory)
        {
            if (CancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.Context, cancellationTokenSource, label);
            }

            var cts = new CancellationTokenSource();
            CancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.Context, cts, label);
        }
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, string label, Func<T, T?, T?> reduce, params Func<CancellationToken, Task<T>>[] fns)
    {
        lock (memoFactory)
        {
            if (CancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.Context, cancellationTokenSource, label);
            }

            var cts = new CancellationTokenSource();
            CancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.Context, cts, label);
        }
    }

    public static ConcurrentRace<T> CreateConcurrentRace<T>(this MemoFactory memoFactory, params Func<CancellationToken, Task<T>>[] fns)
    {
        lock (memoFactory)
        {
            if (CancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentRace<T>(fns, memoFactory.Context, cancellationTokenSource);
            }

            var cts = new CancellationTokenSource();
            CancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentRace<T>(fns, memoFactory.Context, cts);
        }
    }

    public static ConcurrentRace<T> CreateConcurrentRace<T>(this MemoFactory memoFactory, string label, params Func<CancellationToken, Task<T>>[] fns) where T : INumber<T>
    {
        lock (memoFactory)
        {
            if (CancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentRace<T>(fns, memoFactory.Context, cancellationTokenSource, label);
            }

            var cts = new CancellationTokenSource();
            CancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentRace<T>(fns, memoFactory.Context, cts, label);
        }
    }

        public static ConcurrentMap<T> CreateConcurrentMap<T>(this MemoFactory memoFactory, params Func<CancellationToken, Task<T>>[] fns)
    {
        lock (memoFactory)
        {
            if (CancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMap<T>(fns, memoFactory.Context, cancellationTokenSource);
            }

            var cts = new CancellationTokenSource();
            CancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMap<T>(fns, memoFactory.Context, cts);
        }
    }

    public static ConcurrentMap<T> CreateConcurrentMap<T>(this MemoFactory memoFactory, string label, params Func<CancellationToken, Task<T>>[] fns) where T : INumber<T>
    {
        lock (memoFactory)
        {
            if (CancellationTokenSources.TryGetValue(memoFactory, out var cancellationTokenSource))
            {
                return new ConcurrentMap<T>(fns, memoFactory.Context, cancellationTokenSource, label);
            }

            var cts = new CancellationTokenSource();
            CancellationTokenSources.Add(memoFactory, cts);
            return new ConcurrentMap<T>(fns, memoFactory.Context, cts, label);
        }
    }
}