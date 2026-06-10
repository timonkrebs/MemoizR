using System.Collections.Concurrent;

namespace MemoizR.StructuredConcurrency;

public sealed class StructuredResultsJob<T> : StructuredJobBase<ConcurrentDictionary<int, T>>
{
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly Context context;
    private readonly ConcurrentMap<T> @this;
    private readonly CancellationTokenSource cancellationTokenSource;

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

    // Runs one mapped function on its own scope, records its result, and hands the captured
    // sources to the shared accumulator. Extracted from the AddConcurrentWork lambda to keep
    // Cognitive Complexity in budget.
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
        AccumulateSourcesAndObservers(context, @this);
    }

    protected override void HandleSubscriptions()
    {
        @this.Sources = allSources.Distinct().ToArray();
    }
}
