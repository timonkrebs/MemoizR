using System.Collections.Concurrent;

namespace MemoizR.StructuredConcurrency;

public sealed class StructuredResultsJob<T> : StructuredJobBase<ConcurrentDictionary<int, T>>
{
    private IList<IMemoHandlR> allSources = [];


    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly Context context;
    private readonly ConcurrentMap<T> @this;
    private readonly CancellationTokenSource cancellationTokenSource;
    private Lock Lock { get; } = new();

    public StructuredResultsJob(IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns, Context context, ConcurrentMap<T> @this)
    {
        this.fns = fns;
        this.context = context;
        this.@this = @this;
        this.result = new();
        this.cancellationTokenSource = context.CancellationTokenSource!;
    }

    protected override Task AddConcurrentWork(StructuredResourceGroup resourceGroup)
    {
        tasks.AddRange(fns
            .Select((x, i) => new Task<Task>(() => ExecuteFn(x, i, resourceGroup), resourceGroup.Token)));
        return Task.CompletedTask;
    }

    // Runs one mapped function on its own scope, records its result, and rewires source/observer
    // links under Lock. Extracted from the AddConcurrentWork lambda to keep Cognitive Complexity
    // in budget.
    private async Task ExecuteFn(Func<IStructuredResourceGroup, Task<T>> x, int i, StructuredResourceGroup resourceGroup)
    {
        context.ForceNewScope();
        context.ReactionScope.CurrentReaction = @this;
        try
        {
            result!.TryAdd(i, await x(resourceGroup));
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

                for (var j = context.ReactionScope.CurrentGetsIndex; j < allSources.Count(); j++)
                {
                    // Add ourselves to the end of the parent .observers array
                    var source = allSources[j];
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
