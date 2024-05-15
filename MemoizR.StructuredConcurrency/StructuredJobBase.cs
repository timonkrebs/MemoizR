using Nito.AsyncEx;

namespace MemoizR.StructuredConcurrency;

public abstract class StructuredJobBase<T>
{
    protected List<Task> tasks = new();
    protected T? result;
    protected AsyncLock mutex = new();

    protected abstract Task AddConcurrentWork();

    protected virtual void HandleSubscriptions() { }

    public async Task<T> Run()
    {
        try
        {
            List<Task> tasks;
            using (mutex.Lock())
            {
                this.tasks = new();
                await AddConcurrentWork();
                tasks = this.tasks;
            }

            await Task.WhenAll(tasks);
            HandleSubscriptions();
            return result!;
        }
        catch
        {
            var t = Task.WhenAll(tasks);
            await t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (t.Exception != null)
            {
                throw t.Exception;
            }
            throw;
        }
    }
}
