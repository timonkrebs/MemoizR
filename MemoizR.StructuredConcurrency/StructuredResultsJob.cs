using System.Collections.Concurrent;

namespace MemoizR.StructuredConcurrency;

public sealed class StructuredResultsJob<T> : StructuredJobBase<ConcurrentDictionary<int, T>>
{
    private readonly IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns;
    private readonly CancellationTokenSource cancellationTokenSource;

    public StructuredResultsJob(IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns, CancellationTokenSource cancellationTokenSource)
    {
        this.fns = fns;
        this.result = new();
        this.cancellationTokenSource = cancellationTokenSource;
    }

    protected override Task AddConcurrentWork()
    {
        tasks.AddRange(fns
        .Select(async (x, i) => await Task.Run(async () =>
            {
                try
                {
                    result![i] = await x(cancellationTokenSource);
                }
                catch
                {
                    cancellationTokenSource.Cancel();
                    throw;
                }

            }, cancellationTokenSource.Token)
        ));
        return Task.CompletedTask;
    }
}
