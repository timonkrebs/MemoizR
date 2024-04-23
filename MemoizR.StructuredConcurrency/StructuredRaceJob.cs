namespace MemoizR.StructuredConcurrency;

public sealed class StructuredRaceJob<T, R> : StructuredJobBase<T>
{
    private readonly Func<Task<R>> action;
    private readonly IReadOnlyCollection<Func<CancellationTokenSource, R, Task<T>>> fns;
    private readonly CancellationTokenSource innerCancellationTokenSource;
    private readonly CancellationTokenSource groupCancellationTokenSource;
    private bool finished;

    public StructuredRaceJob(Func<Task<R>> action,
        IReadOnlyCollection<Func<CancellationTokenSource, R, Task<T>>> fns, CancellationTokenSource cancellationTokenSource)
    {
        this.action = action;
        this.fns = fns;
        this.result = default;
        this.groupCancellationTokenSource = cancellationTokenSource;
        this.innerCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
    }

    protected override async Task AddConcurrentWork()
    {
        var inputs = await action();
        tasks.AddRange(fns
        .Select(async x =>
        {
            await Task.Run(async () =>
            {
                try
                {
                    result = await x(innerCancellationTokenSource, inputs);
                    finished = true;
                    innerCancellationTokenSource.Cancel();
                }
                catch (TaskCanceledException)
                {
                    if(!finished)
                    {
                        groupCancellationTokenSource.Cancel();
                        throw;
                    }
                }
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
