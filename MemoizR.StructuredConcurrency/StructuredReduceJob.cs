namespace MemoizR.StructuredConcurrency;

public sealed class StructuredReduceJob<T> : StructuredJobBase<T>
{
    private IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns;
    private CancellationTokenSource cancellationTokenSource;
    private readonly Func<T, T, T?> reduce;

    public StructuredReduceJob(IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns, Func<T, T, T?> reduce, CancellationTokenSource cancellationTokenSource)
    : base(cancellationTokenSource.Token)
    {
        this.fns = fns;
        this.reduce = reduce;
        this.cancellationTokenSource = cancellationTokenSource;
    }

    protected override void AddConcurrentWork()
    {
        this.tasks.AddRange(fns
        .Select(async x => await Task.Run(async () =>
            {
                try
                {
                    var r = await x(cancellationTokenSource.Token);
                    lock (fns)
                    {
                        result = reduce(r, result!);
                    }
                }
                catch
                {
                    cancellationTokenSource.Cancel();
                    throw;
                }

            }, cancellationTokenSource.Token)
        ));
    }
}
