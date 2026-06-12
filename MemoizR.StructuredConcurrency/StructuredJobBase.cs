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

    // Folds the sources captured on the given scope into allSources and appends the owning
    // node to each new source's observer down-links, under Lock (children run in parallel).
    // Shared by the reduce/results jobs, whose children each call it after running one mapped fn.
    //
    // A child's reads split between prefix-matches against the owner's previous Sources
    // (CurrentGetsIndex) and fresh captures (CurrentGets); BOTH must be unioned in. The previous
    // replace-on-index-0 branch let each forced-scope child overwrite its siblings' captures, and
    // prefix-matched re-runs contributed nothing at all -- either way HandleSubscriptions then
    // wired the owner's Sources to a subset (or none) of its real dependencies, and the owner's
    // CacheCheck parent scan missed invalidations: a deterministic stale read.
    private protected void AccumulateSourcesAndObservers(ReactionScope scope, IMemoizR owner)
    {
        lock (Lock)
        {
            for (var i = 0; i < scope.CurrentGetsIndex && i < owner.Sources.Length; i++)
            {
                allSources.Add(owner.Sources[i]);
            }
            foreach (var source in scope.CurrentGets)
            {
                allSources.Add(source);
                // Usually a no-op: capture-time eager subscription already wired the link.
                source.AddObserver(owner);
            }
        }
    }

    public async Task<T> Run(CancellationToken token)
    {
        var resourceGroup = new StructuredResourceGroup(token);
        // Built exactly once and reused by the catch path: the old code re-ran the Select there,
        // allocating a second full set of unwrap wrappers over the same children -- and could
        // even await never-started cold tasks if AddConcurrentWork itself had thrown.
        var completion = Task.CompletedTask;
        try
        {
            using (await mutex.LockAsync())
            {
                this.tasks = new();
                await AddConcurrentWork(resourceGroup);
                // Start the cold tasks on the thread pool without blocking the calling thread.
                // (A synchronous mutex.Lock() held across awaits plus a blocking Parallel.ForEach
                // were starving the thread pool, which surfaced as flaky test timeouts.)
                // A sibling that starts and fails INSTANTLY cancels the group token while this
                // loop is still running, which transitions the not-yet-started cold tasks to
                // Canceled -- and Start() on a completed task throws. Such a task behaves
                // exactly like a child canceled mid-run, so skip it; the catch guards the
                // unavoidable window between the status check and Start.
                foreach (var task in tasks)
                {
                    if (task.Status != TaskStatus.Created)
                    {
                        continue;
                    }
                    try
                    {
                        task.Start(TaskScheduler.Default);
                    }
                    catch (InvalidOperationException)
                    {
                        // Canceled between the check and Start: equivalent to the skip above.
                    }
                }
                completion = Task.WhenAll(tasks.Select(async t => await await t));
            }

            await completion;
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
            try
            {
                await completion;
            }
            catch
            {
                // Ignored: rethrown aggregated below.
            }
            if (completion.Exception != null)
            {
                throw completion.Exception;
            }
            throw;
        }
        finally
        {
            await resourceGroup.DisposeResources();
        }
    }
}
