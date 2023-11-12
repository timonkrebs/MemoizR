using System.Collections.Concurrent;

namespace MemoizR.StructuredConcurrency;

public sealed class StructuredResultsJob<T> : StructuredJobBase<BlockingCollection<T>>
{
    private readonly IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns;
    private readonly CancellationTokenSource cancellationTokenSource;

    public StructuredResultsJob(IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns, CancellationTokenSource cancellationTokenSource)
    : base(cancellationTokenSource.Token)
    {
        this.fns = fns;
        this.result = new BlockingCollection<T>(fns.Count);
        this.cancellationTokenSource = cancellationTokenSource;
    }

    protected override void AddConcurrentWork()
    {
        this.tasks.AddRange(fns
        .Select(async x => await Task.Run(async () =>
            {
                try
                {
                    result!.Add(await x(cancellationTokenSource));
                }
                catch (TaskCanceledException) { }
                catch
                {
                    cancellationTokenSource.Cancel();
                    throw;
                }

            }, cancellationTokenSource.Token)
        ));
    }
}
