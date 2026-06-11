namespace MemoizR.StructuredConcurrency;

public sealed class ConcurrentMapReduce<T> : MemoBase<T>
{
    private readonly IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns;
    private readonly Func<T, T, T?> reduce;

    internal ConcurrentMapReduce(IReadOnlyCollection<Func<IStructuredResourceGroup, Task<T>>> fns, Func<T, T, T?> reduce, Context context) : base(context)
    {
        this.fns = fns;
        this.reduce = reduce;
    }

    public void Cancel()
    {
        Context.CancellationTokenSource?.Cancel();
    }

    internal override Task<T> ComputeAsync()
    {
        return new StructuredReduceJob<T>(fns, reduce, Context, this).Run(Context.CancellationTokenSource!.Token);
    }

    // The reduce job's HandleSubscriptions wires the source/observer links from the accumulated
    // child captures; letting MemoBase ALSO rewire from the shared scope's raw CurrentGets ran a
    // second, competing pass that overwrote the deduplicated Sources with arrival-ordered
    // duplicates and strip/re-added the very observer links the job just wrote (a Set landing in
    // that window was lost). One wiring path only -- same as ConcurrentMap.
    internal override bool RewireOwnLinks => false;

    // Deliberately NO finalizer: the CancellationTokenSource is CONTEXT-wide and shared by every
    // evaluation in flight, so a finalizer calling Cancel() would abort unrelated work at an
    // arbitrary GC-determined moment on the finalizer thread.
}
