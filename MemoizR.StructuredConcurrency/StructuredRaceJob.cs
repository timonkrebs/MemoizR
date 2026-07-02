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

    // First-successful-racer latch: the value, the winner flag and the capture-closing callback
    // are claimed TOGETHER by exactly one racer, so a slower successful sibling can neither
    // overwrite the result nor detach it from the evidence captured for it.
    private int winClaimed;

    // Invoked exactly once, on the winning racer's flow, the moment its result is recorded --
    // BEFORE the losers are cancelled. ConcurrentRace closes its causality-stamp capture here:
    // reads performed by losing branches after this point did not feed the winning value and
    // must not widen its published stamp (issue #39).
    internal Action? OnWinnerSelected { get; init; }

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
        // The cold tasks deliberately carry NO cancellation token: a winner that completes
        // before a sibling's task is even started would otherwise cancel that task pre-start,
        // skipping the forgiving catch below entirely -- the raw TaskCanceledException then
        // fails a race that has a perfectly good winner (seen on slow CI runners). Cancellation
        // flows to the branch BODIES through raceResourceGroup.Token instead, where the catch
        // owns the winner-aware forgiveness.
        tasks.AddRange(fns.Select(x => new Task<Task>(async () =>
            {
                try
                {
                    var candidate = await x(raceResourceGroup, inputs);
                    if (Interlocked.CompareExchange(ref winClaimed, 1, 0) != 0)
                    {
                        return; // a sibling already won; this result never becomes visible
                    }
                    finished = true;
                    result = candidate;
                    OnWinnerSelected?.Invoke();
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
            })
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
