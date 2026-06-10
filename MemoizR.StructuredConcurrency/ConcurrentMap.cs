namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentMap<T> : MemoBase<IEnumerable<T>>
{
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;

    internal ConcurrentMap(IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns, Context context) : base(context)
    {
        this.fns = fns;
    }

    public void Cancel()
    {
        Context.CancellationTokenSource?.Cancel();
    }

    internal override async Task<IEnumerable<T>> ComputeAsync()
    {
        return (await new StructuredResultsJob<T>(fns, Context!, this).Run(Context.CancellationTokenSource!.Token)).Select(x => x.Value);
    }

    // The results job's parallel children capture and wire the source/observer links themselves
    // (each on its own forced scope); rewiring from this node's scope would see nothing.
    internal override bool RewireOwnLinks => false;

    // The value is a sequence; observers should only be dirtied when the elements changed, not
    // when a recompute produced a new-but-equal enumerable. Null-tolerant because the value is
    // unset before the first computation.
    internal override bool ValuesEqual(IEnumerable<T> oldValue, IEnumerable<T> newValue)
    {
        return (oldValue ?? []).SequenceEqual(newValue ?? []);
    }

    ~ConcurrentMap()
    {
        Cancel();
    }
}
