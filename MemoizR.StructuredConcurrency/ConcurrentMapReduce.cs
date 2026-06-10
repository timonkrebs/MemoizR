namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentMapReduce<T> : MemoBase<T>
{
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly Func<T, T, T?> reduce;

    internal ConcurrentMapReduce(IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns, Func<T, T, T?> reduce, Context context) : base(context)
    {
        this.fns = fns;
        this.reduce = reduce;
    }

    public void Cancel()
    {
        Context.CancellationTokenSource?.Cancel();
    }

    internal override Task<T> ComputeAsync()
    {
        return new StructuredReduceJob<T>(fns, reduce, Context, this).Run(Context.CancellationTokenSource!.Token);
    }

    ~ConcurrentMapReduce()
    {
        Cancel();
    }
}
