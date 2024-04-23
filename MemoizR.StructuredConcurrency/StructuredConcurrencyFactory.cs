using System.Numerics;
using MemoizR.StructuredConcurrency;

namespace MemoizR;
public static class StructuredConcurrencyFactory
{
    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, params Func<CancellationTokenSource, Task<T>>[] fns) where T : INumber<T>
    {
        return CreateConcurrentMapReduce(memoFactory, "Numeric Concurrent Reduce", fns);
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, string label, params Func<CancellationTokenSource, Task<T>>[] fns) where T : INumber<T>
    {
        return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.Context)
        {
            Label = label
        };
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, Func<T, T?, T> reduce, params Func<CancellationTokenSource, Task<T>>[] fns)
    {
        return CreateConcurrentMapReduce(memoFactory, "Concurrent Reduce", reduce, fns);
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, string label, Func<T, T?, T> reduce, params Func<CancellationTokenSource, Task<T>>[] fns)
    {
        return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.Context)
        {
            Label = label
        };
    }

    public static ConcurrentRace<T, R> CreateConcurrentRace<T, R>(this MemoFactory memoFactory, Func<Task<R>> resolver, params Func<CancellationTokenSource, R, Task<T>>[] fns)
    {
        return CreateConcurrentRace(memoFactory, "Concurrent Race", resolver, fns);
    }

    public static ConcurrentRace<T, R> CreateConcurrentRace<T, R>(this MemoFactory memoFactory, string label, Func<Task<R>> resolver, params Func<CancellationTokenSource, R, Task<T>>[] fns)
    {
        return new ConcurrentRace<T, R>(resolver, fns, memoFactory.Context)
        {
            Label = label
        };
    }

    public static ConcurrentMap<T> CreateConcurrentMap<T>(this MemoFactory memoFactory, params Func<CancellationTokenSource, Task<T>>[] fns)
    {
        return CreateConcurrentMap(memoFactory, "Concurrent Map", fns);
    }

    public static ConcurrentMap<T> CreateConcurrentMap<T>(this MemoFactory memoFactory, string label, params Func<CancellationTokenSource, Task<T>>[] fns)
    {
        return new ConcurrentMap<T>(fns, memoFactory.Context)
        {
            Label = label
        };
    }
}