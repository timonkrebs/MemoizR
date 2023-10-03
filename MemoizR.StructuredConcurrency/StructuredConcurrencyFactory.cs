namespace MemoizR.Reactive;

using System.Numerics;
using System.Text.RegularExpressions;
using MemoizR.StructuredConcurrency;

public class StructuredConcurrencyFactory : MemoFactory
{
    public StructuredConcurrencyFactory(string? contextKey = null) : base(contextKey) { }

    public ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(params Func<Task<T>>[] fns) where T : INumber<T>
    {
        return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, context);
    }

    public ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(Func<T, T, T> reduce, params Func<Task<T>>[] fns)
    {
        return new ConcurrentMapReduce<T>(fns, reduce, context);
    }

}