namespace MemoizR.StructuredConcurrency;

// A structured job that accumulates the dependencies its parallel children read and, after the
// run, wires them onto an owning node's Sources. The two map-shaped jobs derive from this:
// StructuredResultsJob (ConcurrentMap) and StructuredReduceJob (ConcurrentMapReduce). The race
// (StructuredRaceJob) deliberately does NOT -- it captures dependencies eagerly during evaluation
// and owns no node to wire, so it stays on StructuredJobBase with the no-op HandleSubscriptions.
// Encoding that split in the type removes the per-call owner parameter, the nullable owner field,
// and the "is the owner set?" runtime check a single shared base would otherwise need.
public abstract class OwnedStructuredJob<T> : StructuredJobBase<T>
{
    // The node whose Sources the accumulated child captures wire onto.
    private protected readonly IMemoizR owner;

    // Sources captured by the parallel child tasks, accumulated under Lock; HandleSubscriptions
    // reads them after the Task.WhenAll join (the join is the barrier).
    private IList<IMemoHandlR> allSources = [];
    private protected Lock Lock { get; } = new();

    private protected OwnedStructuredJob(IMemoizR owner)
    {
        this.owner = owner;
    }

    // Publish the deduplicated captured sources onto the owner after the WhenAll join.
    protected override void HandleSubscriptions()
    {
        owner.Sources = allSources.Distinct().ToArray();
    }

    // Folds the sources captured on the given scope into allSources and appends the owner to each
    // new source's observer down-links, under Lock (children run in parallel). Shared by the
    // reduce/results jobs, whose children each call it after running one mapped fn.
    //
    // A child's reads split between prefix-matches against the owner's previous Sources
    // (CurrentGetsIndex) and fresh captures (CurrentGets); BOTH must be unioned in. The previous
    // replace-on-index-0 branch let each forced-scope child overwrite its siblings' captures, and
    // prefix-matched re-runs contributed nothing at all -- either way HandleSubscriptions then
    // wired the owner's Sources to a subset (or none) of its real dependencies, and the owner's
    // CacheCheck parent scan missed invalidations: a deterministic stale read.
    private protected void AccumulateSourcesAndObservers(ReactionScope scope)
    {
        lock (Lock)
        {
            foreach (var source in owner.Sources.Take(scope.CurrentGetsIndex))
            {
                allSources.Add(source);
            }
            foreach (var source in scope.CurrentGets)
            {
                allSources.Add(source);
                // Usually a no-op: capture-time eager subscription already wired the link.
                source.AddObserver(owner);
            }
        }
    }
}
