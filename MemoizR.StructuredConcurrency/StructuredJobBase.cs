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
            using (mutex.Lock())
            {
                this.tasks = new();
                await AddConcurrentWork(resourceGroup);
                tasks = this.tasks;
                Parallel.ForEach(tasks, (task) => task.Start());
            }

            await Task.WhenAll(tasks.Select(async t => await await t));
            HandleSubscriptions();
            return result!;
        }
        catch
        {
            var t = Task.WhenAll(tasks.Select(async t => await await t));
            await t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (t.Exception != null)
            {
                throw t.Exception;
            }
            throw;
        }
        finally
        {
            await resourceGroup.DisposeResources();
        }
    }
}
