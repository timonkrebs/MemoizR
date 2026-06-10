using Nito.AsyncEx;

namespace MemoizR.StructuredConcurrency;

public abstract class StructuredJobBase<T>
{
    protected List<Task<Task>> tasks = new();
    protected T? result;
    protected AsyncLock mutex = new();

    // Sources captured by the parallel child tasks, accumulated under Lock; the owning node's
    // HandleSubscriptions reads them after the Task.WhenAll join (the join is the barrier).
    private protected IList<IMemoHandlR> allSources = [];
    private protected Lock Lock { get; } = new();

    protected abstract Task AddConcurrentWork(StructuredResourceGroup resourceGroup);

    protected virtual void HandleSubscriptions() { }

    // Folds the sources captured on the current scope into allSources and appends the owning
    // node to each new source's observer down-links, under Lock (children run in parallel).
    // Shared by the reduce/results jobs, whose children each call it after running one mapped fn.
    private protected void AccumulateSourcesAndObservers(Context context, IMemoizR owner)
    {
        lock (Lock)
        {
            // if the sources have changed, update source & observer links
            if (context.ReactionScope.CurrentGets.Length > 0)
            {
                // update source up links
                if (allSources.Any() && context.ReactionScope.CurrentGetsIndex > 0)
                {
                    allSources = [.. allSources, .. context.ReactionScope.CurrentGets];
                }
                else
                {
                    allSources = context.ReactionScope.CurrentGets;
                }

                for (var i = context.ReactionScope.CurrentGetsIndex; i < allSources.Count; i++)
                {
                    // Add ourselves to the end of the parent .observers array
                    var source = allSources[i];
                    source.Observers = !source.Observers.Any()
                        ? [new(owner)]
                        : [.. source.Observers, new(owner)];
                }
            }
        }
    }

    public async Task<T> Run(CancellationToken token)
    {
        var resourceGroup = new StructuredResourceGroup(token);
        try
        {
            List<Task<Task>> tasks;
            using (await mutex.LockAsync())
            {
                this.tasks = new();
                await AddConcurrentWork(resourceGroup);
                tasks = this.tasks;
                // Start the cold tasks on the thread pool without blocking the calling thread.
                // (A synchronous mutex.Lock() held across awaits plus a blocking Parallel.ForEach
                // were starving the thread pool, which surfaced as flaky test timeouts.)
                foreach (var task in tasks)
                {
                    task.Start(TaskScheduler.Default);
                }
            }

            await Task.WhenAll(tasks.Select(async t => await await t));
            HandleSubscriptions();
            return result!;
        }
        catch
        {
            // Surface every fault aggregated. `await Task.WhenAll` only rethrows the first
            // exception, so we wait for completion and then throw the full AggregateException.
            // We use an explicit try/catch rather than ConfigureAwaitOptions.SuppressThrowing,
            // which the Coyote rewriter does not honour (under instrumentation it rethrew a
            // single unwrapped exception, breaking AggregateException expectations).
            var all = Task.WhenAll(tasks.Select(async t => await await t));
            try
            {
                await all;
            }
            catch
            {
                // Ignored: rethrown aggregated below.
            }
            if (all.Exception != null)
            {
                throw all.Exception;
            }
            throw;
        }
        finally
        {
            await resourceGroup.DisposeResources();
        }
    }
}
