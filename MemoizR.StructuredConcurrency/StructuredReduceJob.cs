namespace MemoizR.StructuredConcurrency;

public sealed class StructuredReduceJob<T> : StructuredJobBase<T>
{
    private IList<IMemoHandlR> allSources = [];

    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly Func<T, T, T?> reduce;
    private readonly Context context;
    private readonly ConcurrentMapReduce<T> @this;
    private Lock Lock { get; } = new();

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

    // Runs one mapped function, folds its result into the accumulator, and rewires source/observer
    // links under Lock. Extracted from the AddConcurrentWork lambda to keep Cognitive Complexity
    // in budget.
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
        lock (Lock)
        {
            // if the sources have changed, update source & observer links
            if (context.ReactionScope.CurrentGets.Length > 0)
            {
                // update source up links
                if (allSources.Count > 0 && context.ReactionScope.CurrentGetsIndex > 0)
                {
                    allSources = [.. allSources, .. context.ReactionScope.CurrentGets];
                }
                else
                {
                    allSources = context.ReactionScope.CurrentGets;
                }

                for (var i = context.ReactionScope.CurrentGetsIndex; i < allSources.Count(); i++)
                {
                    // Add ourselves to the end of the parent .observers array
                    var source = allSources[i];
                    source.Observers = source.Observers.Length == 0
                        ? [new(@this)]
                        : [.. source.Observers, new(@this)];
                }
            }
        }
    }

    protected override void HandleSubscriptions()
    {
        @this.Sources = allSources.Distinct().ToArray();
    }
}
