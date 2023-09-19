namespace MemoizR;

public class Context
{
    internal ReaderWriterLockSlim contextLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    internal dynamic? CurrentReaction = null;  // ToDo type it to get rid of dynamic type!
    internal IMemoHandlR[] CurrentGets = Array.Empty<IMemoHandlR>();
    internal int CurrentGetsIndex;
}
