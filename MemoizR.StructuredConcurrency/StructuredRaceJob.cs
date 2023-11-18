namespace MemoizR.StructuredConcurrency;

public sealed class StructuredRaceJob<T> : StructuredJobBase<T>
{
    private readonly IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns;
    private readonly CancellationTokenSource innerCancellationTokenSource;
    private readonly CancellationTokenSource groupCancellationTokenSource;

    public StructuredRaceJob(IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns, CancellationTokenSource cancellationTokenSource)
    {
        this.fns = fns;
        this.result = default;
        this.groupCancellationTokenSource = cancellationTokenSource;
        this.innerCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
    }

    protected override void AddConcurrentWork()
    {
        this.tasks.AddRange(fns
        .Select(async x =>
        {
            await Task.Run(async () =>
            {
                try
                {
                    result = await x(innerCancellationTokenSource);
                    innerCancellationTokenSource.Cancel();
                }
                catch (TaskCanceledException) { }
                catch
                {
                    groupCancellationTokenSource.Cancel();
                    throw;
                }
            }, innerCancellationTokenSource.Token);
        }
        ));
    }
}
