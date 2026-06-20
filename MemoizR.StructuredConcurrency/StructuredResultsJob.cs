using System.Collections.Concurrent;

namespace MemoizR.StructuredConcurrency;

public sealed class StructuredResultsJob<T> : OwnedStructuredJob<ConcurrentDictionary<int, T>>
{
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly Context context;
    private readonly CancellationTokenSource cancellationTokenSource;

    public StructuredResultsJob(IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns, Context context, ConcurrentMap<T> @this) : base(@this)
    {
        this.fns = fns;
        this.context = context;
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
        // The local is the scope's ONLY strong root (the registry holds it weakly): without it a
        // GC during the awaited fn collects the scope, the fn's reads resolve a resurrected scope
        // whose CurrentReaction is null, and the child's dependencies are silently not captured.
        var scope = context.ForceNewScope();
        scope.CurrentReaction = owner;
        try
        {
            result!.TryAdd(i, await x(resourceGroup));
        }
        catch
        {
            cancellationTokenSource.Cancel();
            throw;
        }
        AccumulateSourcesAndObservers(scope);
        GC.KeepAlive(scope);
    }
}
