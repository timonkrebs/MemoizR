namespace MemoizR.StructuredConcurrency;

public sealed class StructuredRaceJob<T, R> : StructuredJobBase<T>, IDisposable
{
    private readonly Func<Task<R>> action;
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, R, Task<T>>> fns;
    private readonly CancellationTokenSource innerCancellationTokenSource;
    private readonly CancellationTokenSource groupCancellationTokenSource;
    // First-successful-racer latch: the value and the capture-closing callback are claimed
    // TOGETHER by exactly one racer, so a slower successful sibling can neither overwrite the
    // result nor detach it from the evidence captured for it. The loser-forgiveness in the
    // catch below keys off this same latch (not a separate flag set later), so there is no gap
    // in which a faulting sibling could fail a race whose winner is already claimed.
    private int winClaimed;

    // Invoked exactly once, on the winning racer's flow, with the 1-based index of the winning
    // branch, BEFORE the losers are cancelled. ConcurrentRace closes its causality-stamp
    // capture here and later seals it to this branch: reads performed by losing branches did
    // not feed the winning value and must not widen its published stamp (issue #39).
    internal Action<int>? OnWinnerSelected { get; init; }

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
        tasks.AddRange(fns.Select((x, index) => new Task<Task>(async () =>
            {
                // Tag this branch's flow (1-based) so its tracked reads are attributed to it in
                // the race's stamp capture; the AsyncLocal write lives in the branch task's own
                // ExecutionContext and never leaks to the parent flow.
                RaceBranchFlow.Current.Value = index + 1;
                try
                {
                    // A branch whose cold task starts only after a sibling already won (or the
                    // group was cancelled) must not run user code against a dead token; the
                    // throw lands in the catch below, which forgives it when a winner exists.
                    raceResourceGroup.Token.ThrowIfCancellationRequested();

                    var candidate = await x(raceResourceGroup, inputs);
                    if (Interlocked.CompareExchange(ref winClaimed, 1, 0) != 0)
                    {
                        return; // a sibling already won; this result never becomes visible
                    }
                    // Close the capture BEFORE recording the winning value. The value only
                    // becomes visible at Run's WhenAll barrier -- strictly after this whole
                    // block -- and the close is atomic against sibling records (both run under
                    // Context.Lock), so this ordering is not load-bearing; it keeps the
                    // invariant textual: once the winner exists anywhere, the capture is shut.
                    OnWinnerSelected?.Invoke(index + 1);
                    result = candidate;
                    innerCancellationTokenSource.Cancel();
                }
                catch
                {
                    // A loser that faults (including via cancellation) after a winner was
                    // claimed must not turn the completed race into a failure; propagate only
                    // while no winner exists. Keyed off the win latch itself -- not a flag set
                    // afterwards -- so there is no instruction window in which a sibling's
                    // fault could sink a race whose winner is already claimed.
                    if (Volatile.Read(ref winClaimed) == 0)
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
