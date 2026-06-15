namespace MemoizR.StructuredConcurrency;

public sealed class StructuredRaceJob<T, R> : StructuredJobBase<T>, IDisposable
{
    private readonly Func<Task<R>> action;
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, R, Task<T>>> fns;
    private readonly CancellationTokenSource innerCancellationTokenSource;
    private readonly CancellationTokenSource groupCancellationTokenSource;
    // Cross-task winner flag: once any racer succeeds, slower siblings may observe cancellation
    // or fault during teardown, but they must not turn a completed race into a failed one.
    private volatile bool finished;

    public StructuredRaceJob(Func<Task<R>> action,
        IReadOnlyCollection<Func<IStructuredResourceGroup, R, Task<T>>> fns, CancellationTokenSource cancellationTokenSource)
    {
        this.action = action;
        this.fns = fns;
        this.result = default;
        this.groupCancellationTokenSource = cancellationTokenSource;
        this.innerCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
    }

    private sealed class RaceResourceGroup : IStructuredResourceGroup
    {
        private readonly IStructuredResourceGroup parent;
        public CancellationToken Token { get; }

        public RaceResourceGroup(IStructuredResourceGroup parent, CancellationToken token)
        {
            this.parent = parent;
            this.Token = token;
        }

        public void AddResource(IDisposable resource) => parent.AddResource(resource);
        public void AddResource(IAsyncDisposable resource) => parent.AddResource(resource);
    }

    protected override async Task AddConcurrentWork(StructuredResourceGroup resourceGroup)
    {
        var inputs = await action();
        var raceResourceGroup = new RaceResourceGroup(resourceGroup, innerCancellationTokenSource.Token);
        tasks.AddRange(fns.Select(x => new Task<Task>(async () =>
            {
                try
                {
                    result = await x(raceResourceGroup, inputs);
                    finished = true;
                    innerCancellationTokenSource.Cancel();
                }
                catch (OperationCanceledException)
                {
                    if (!finished)
                    {
                        groupCancellationTokenSource.Cancel();
                        throw;
                    }
                }
                catch
                {
                    if (!finished)
                    {
                        groupCancellationTokenSource.Cancel();
                        throw;
                    }
                }
            }, innerCancellationTokenSource.Token)
            ));

    }

    public void Dispose()
    {
        innerCancellationTokenSource.Dispose();
    }
}
