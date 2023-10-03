namespace MemoizR.StructuredConcurrency;

public sealed class StructuredJob<T>
{
    private IReadOnlyCollection<Func<Task<T>>> fns;
    private List<Task> tasks = new List<Task>();
    private CancellationTokenSource cancellationTokenSource;
    private readonly Func<T, T, T?> reduce;
    private T? aggregate;

    public StructuredJob(IReadOnlyCollection<Func<Task<T>>> fns, Func<T, T, T?> reduce, T? aggregate, CancellationToken? cancellationToken = null)
    {
        this.fns = fns;
        this.reduce = reduce;
        this.aggregate = aggregate;
        this.cancellationTokenSource = 
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? new CancellationToken());
    }

    public void AddConcurrentWork(IReadOnlyCollection<Func<Task<T>>> fns)
    {
        lock (this)
        {
            this.tasks.AddRange(fns
            .Select(async x => await Task.Run(async() =>
                {
                    var r = await x();
                    lock (fns)
                    {
                        aggregate = reduce(r, aggregate!);
                    }
                }, cancellationTokenSource.Token)
            ));
        }
    }

    public Task<T?> Run()
    {
        try
        {
            AddConcurrentWork(this.fns);
            return WaitAll();
        }
        catch
        {
            cancellationTokenSource.Cancel();
            throw;
        }
    }

    private async Task<T?> WaitAll()
    {
        if (!this.tasks.Any()) return aggregate;
        List<Task> tasks;
        lock (this)
        {
            tasks = this.tasks;
            this.tasks = new List<Task>();
        }

        try
        {
            await Task.WhenAll(tasks.ToArray());
            return aggregate;
        }
        catch
        {
            cancellationTokenSource.Cancel();
            throw;
        }
    }
}
