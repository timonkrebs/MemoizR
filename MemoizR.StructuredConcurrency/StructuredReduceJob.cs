namespace MemoizR.StructuredConcurrency;

public sealed class StructuredReduceJob<T> : StructuredJobBase<T>
{
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly Func<T, T, T?> reduce;
    private readonly Context context;
    private readonly ConcurrentMapReduce<T> @this;

    public StructuredReduceJob(IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns, Func<T, T, T?> reduce, Context context, ConcurrentMapReduce<T> @this)
    {
        this.fns = fns;
        this.reduce = reduce;
        this.context = context;
        this.@this = @this;
        this.cancellationTokenSource = context.CancellationTokenSource!;
    }

    protected override Task AddConcurrentWork(StructuredResourceGroup resourceGroup)
    {
        tasks.AddRange(fns
            .Select(x => new Task<Task>(() => ExecuteFn(x, resourceGroup), resourceGroup.Token)));
        return Task.CompletedTask;
    }

    // Runs one mapped function, folds its result into the accumulator under Lock, and hands the
    // captured sources to the shared accumulator. Extracted from the AddConcurrentWork lambda to
    // keep Cognitive Complexity in budget.
    private async Task ExecuteFn(Func<IStructuredResourceGroup, Task<T>> x, StructuredResourceGroup resourceGroup)
    {
        try
        {
            var r = await x(resourceGroup);
            lock (Lock)
            {
                result = reduce(r, result!);
            }
        }
        catch
        {
            cancellationTokenSource.Cancel();
            throw;
        }
        AccumulateSourcesAndObservers(context, @this);
    }

    protected override void HandleSubscriptions()
    {
        @this.Sources = allSources.Distinct().ToArray();
    }
}
