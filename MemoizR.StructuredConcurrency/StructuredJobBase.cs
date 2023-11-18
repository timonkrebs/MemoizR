namespace MemoizR.StructuredConcurrency;

public abstract class StructuredJobBase<T>
{
    protected List<Task> tasks = new List<Task>();
    private CancellationTokenSource cancellationTokenSource;
    protected T? result;

    public StructuredJobBase(CancellationToken? cancellationToken = null)
    {
        cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? new CancellationToken());
    }

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
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            
            cancellationTokenSource.Cancel();
            throw;
        }
    }
}
