namespace MemoizR;

internal interface IMemoHandlR
{
    WeakReference<IMemoizR>[] Observers { get; set; }
    IMemoHandlR[] Sources { get; set; }

    // Atomic add-if-absent / remove on the observer down-links. Observer membership is mutated
    // from three different lock domains (dependency capture under Context.Lock, link rewiring
    // under a flow's ContextLock + node mutex, job accumulation under the job Lock), so the
    // read-modify-write must be serialized per node or concurrent whole-array swaps lose entries.
    void AddObserver(IMemoizR observer);
    void RemoveObserver(IMemoizR observer);
}
