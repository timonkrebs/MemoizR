using Nito.AsyncEx;

namespace MemoizR.StructuredConcurrency;

public abstract class StructuredJobBase<T>
{
    protected List<Task<Task>> tasks = new();
    protected T? result;
    protected AsyncLock mutex = new();

    protected abstract Task AddConcurrentWork(StructuredResourceGroup resourceGroup);

    protected virtual void HandleSubscriptions() { }

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
