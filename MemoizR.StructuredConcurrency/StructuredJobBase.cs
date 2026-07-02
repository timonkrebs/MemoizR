using System.Runtime.ExceptionServices;
using Nito.AsyncEx;

namespace MemoizR.StructuredConcurrency;

public abstract class StructuredJobBase<T>
{
    protected List<Task<Task>> tasks = new();
    protected T? result;
    protected AsyncLock mutex = new();

    protected abstract Task AddConcurrentWork(StructuredResourceGroup resourceGroup);

    // Called on the success path after the run completes. The source-wiring jobs override it (via
    // OwnedStructuredJob) to publish their captured dependencies onto the owning node; the race,
    // which captures eagerly during evaluation and owns no node, keeps this no-op.
    protected virtual void HandleSubscriptions() { }

    public async Task<T> Run(CancellationToken token)
    {
        var resourceGroup = new StructuredResourceGroup(token);
        // Built exactly once and reused by the catch path: the old code re-ran the Select there,
        // allocating a second full set of unwrap wrappers over the same children -- and could
        // even await never-started cold tasks if AddConcurrentWork itself had thrown.
        var completion = Task.CompletedTask;
        // The job-body fault (if any) is captured here rather than thrown straight away, so that
        // resource disposal still runs afterwards and a disposal fault can be COMBINED with it
        // instead of replacing it. Throwing directly out of the disposal `finally` used to mask
        // the original job failure -- the diagnosability problem this method now guards against.
        ExceptionDispatchInfo? jobException = null;
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
        }
        catch (Exception bodyException)
        {
            // Surface every fault aggregated. `await Task.WhenAll` only rethrows the first
            // exception, so we wait for completion and then capture the full AggregateException.
            // We use an explicit try/catch rather than ConfigureAwaitOptions.SuppressThrowing,
            // which the Coyote rewriter does not honour (under instrumentation it rethrew a
            // single unwrapped exception, breaking AggregateException expectations).
            try
            {
                await completion;
            }
            catch
            {
                // Ignored: surfaced via completion.Exception below.
            }
            // The parallel children fault `completion`; anything else (AddConcurrentWork, the
            // mutex, HandleSubscriptions) only surfaces as bodyException.
            jobException = ExceptionDispatchInfo.Capture(completion.Exception ?? bodyException);
        }

        // Dispose on BOTH the success and failure paths -- but deliberately outside a `finally`,
        // so a disposal fault can be merged with an in-flight job fault rather than overwriting it
        // (a throwing `finally` silently discards the exception that is already propagating).
        Exception? disposalException = null;
        try
        {
            await resourceGroup.DisposeResources();
        }
        catch (Exception ex)
        {
            disposalException = ex;
        }

        if (jobException != null && disposalException != null)
        {
            // Both the job and its cleanup failed: report both, flattening the two nested
            // AggregateExceptions into one level so callers see a single flat list of causes.
            throw new AggregateException(jobException.SourceException, disposalException).Flatten();
        }
        if (disposalException != null)
        {
            // Job succeeded but cleanup failed: surface the disposal fault as-is.
            ExceptionDispatchInfo.Throw(disposalException);
        }
        // Re-throw a job-only fault preserving its original stack trace, or return the computed
        // result when nothing failed.
        jobException?.Throw();
        return result!;
    }
}
