namespace MemoizR.StructuredConcurrency;

public abstract class StructuredJobBase<T>
{
    protected List<Task> tasks = new List<Task>();
    protected T? result;

    protected abstract void AddConcurrentWork();

    public async Task<T> Run()
    {
        try
        {
            List<Task> tasks;
            lock (this)
            {
                this.tasks = new List<Task>();
                AddConcurrentWork();
                tasks = this.tasks;
            }

            await Task.WhenAll(tasks);
            return result!;
        }
        catch
        {
            var t = Task.WhenAll(tasks);
            await t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if(t.Exception != null) {
                throw t.Exception;
            }
            throw;
        }
    }
}
