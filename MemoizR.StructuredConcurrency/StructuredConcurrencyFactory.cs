using System.Numerics;
using MemoizR.StructuredConcurrency;

namespace MemoizR;
public static class StructuredConcurrencyFactory
{
    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, params Func<CancellationTokenSource, Task<T>>[] fns) where T : INumber<T>
    {
        return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.Context);
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, Func<T, T?, T?> reduce, params Func<CancellationTokenSource, Task<T>>[] fns)
    {
        return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.Context);
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, string label, params Func<CancellationTokenSource, Task<T>>[] fns) where T : INumber<T>
    {
        return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, memoFactory.Context, label);
    }

    public static ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(this MemoFactory memoFactory, string label, Func<T, T?, T?> reduce, params Func<CancellationTokenSource, Task<T>>[] fns)
    {
        return new ConcurrentMapReduce<T>(fns, reduce, memoFactory.Context, label);
    }

    public static ConcurrentRace<T> CreateConcurrentRace<T>(this MemoFactory memoFactory, params Func<CancellationTokenSource, Task<T>>[] fns)
    {
        return new ConcurrentRace<T>(fns, memoFactory.Context);
    }

    public static ConcurrentRace<T> CreateConcurrentRace<T>(this MemoFactory memoFactory, string label, params Func<CancellationTokenSource, Task<T>>[] fns) where T : INumber<T>
    {
        return new ConcurrentRace<T>(fns, memoFactory.Context, label);
    }

    public static ConcurrentMap<T> CreateConcurrentMap<T>(this MemoFactory memoFactory, params Func<CancellationTokenSource, Task<T>>[] fns)
    {

        return new ConcurrentMap<T>(fns, memoFactory.Context);
    }

    public static ConcurrentMap<T> CreateConcurrentMap<T>(this MemoFactory memoFactory, string label, params Func<CancellationTokenSource, Task<T>>[] fns) where T : INumber<T>
    {
        return new ConcurrentMap<T>(fns, memoFactory.Context, label);
    }
}