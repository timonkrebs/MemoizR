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
                catch
                {
                    // A loser that faults (including via cancellation) after a winner finished must
                    // not turn the completed race into a failure; propagate only while no winner
                    // has been recorded yet.
                    if (!finished)
                    {
                        groupCancellationTokenSource.Cancel();
                        throw;
                    }
                }
            }, innerCancellationTokenSource.Token)
            ));

    }

    // Deterministic cleanup of the per-race linked source after the job completes (the WhenAll
    // join in Run is the barrier, so no racer touches it afterwards). Cleanup hygiene, not a leak
    // fix: per .jules/sentinel.md the parent Context source is depth-refcounted and its linked
    // island is GC-collected when the evaluation tree unwinds.
    public void Dispose()
    {
        innerCancellationTokenSource.Dispose();
    }
}
