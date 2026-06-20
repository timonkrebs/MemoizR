namespace MemoizR.StructuredConcurrency;

public sealed class StructuredReduceJob<T> : OwnedStructuredJob<T>
{
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly Func<T, T, T?> reduce;
    private readonly Context context;

    public StructuredReduceJob(IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns, Func<T, T, T?> reduce, Context context, ConcurrentMapReduce<T> @this) : base(@this)
    {
        this.fns = fns;
        this.reduce = reduce;
        this.context = context;
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
        // Children share the owning Get's flow scope (kept alive by that Get's strong local);
        // resolve it once instead of re-probing the registry per access.
        var scope = context.ReactionScope;
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
        AccumulateSourcesAndObservers(scope);
    }
}
