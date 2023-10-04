
using System.Numerics;
using MemoizR.StructuredConcurrency;

namespace MemoizR.Reactive;
public class StructuredConcurrencyFactory : MemoFactory
{
    private CancellationTokenSource cancellationTokenSource = new();
    public StructuredConcurrencyFactory(string? contextKey = null) : base(contextKey) {

     }

    public ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(params Func<CancellationToken, Task<T>>[] fns) where T : INumber<T>
    {
        return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, context, cancellationTokenSource);
    }

    public ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(Func<T, T?, T?> reduce, params Func<CancellationToken, Task<T>>[] fns)
    {
        return new ConcurrentMapReduce<T>(fns, reduce, context, cancellationTokenSource);
    }

    public ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(string label, params Func<CancellationToken, Task<T>>[] fns) where T : INumber<T>
    {
        return new ConcurrentMapReduce<T>(fns, (v, a) => v + a, context, cancellationTokenSource, label);
    }

    public ConcurrentMapReduce<T> CreateConcurrentMapReduce<T>(string label, Func<T, T?, T?> reduce, params Func<CancellationToken, Task<T>>[] fns)
    {
        return new ConcurrentMapReduce<T>(fns, reduce, context, cancellationTokenSource, label);
    }

}