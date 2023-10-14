namespace MemoizR.StructuredConcurrency;

public sealed class StructuredRaceJob<T> : StructuredJobBase<T>
{
    private IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns;
    private CancellationTokenSource innerCancellationTokenSource = new CancellationTokenSource();
    private CancellationTokenSource groupCancellationTokenSource;

    public StructuredRaceJob(IReadOnlyCollection<Func<CancellationToken, Task<T>>> fns, CancellationTokenSource cancellationTokenSource)
    : base(cancellationTokenSource.Token)
    {
        this.fns = fns;
        this.result = default;
        this.groupCancellationTokenSource = cancellationTokenSource;
        this.innerCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
    }

    protected override void AddConcurrentWork()
    {
        this.tasks.AddRange(fns
        .Select(async x => await Task.Run(async () =>
            {
                try
                {
                    result = await x(innerCancellationTokenSource.Token);
                    innerCancellationTokenSource.Cancel();
                }
                catch
                {
                    groupCancellationTokenSource.Cancel();
                    throw;
                }

            }, innerCancellationTokenSource.Token)
        ));
    }
}
