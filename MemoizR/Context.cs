namespace MemoizR;

internal class Context
{
    internal ManualResetEvent WaitHandle = new ManualResetEvent(false);
    internal int WaitCount;

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    internal dynamic? CurrentReaction = null;  // ToDo type it to get rid of dynamic type but without forcing boxing
    internal IMemoHandlR[] CurrentGets = Array.Empty<IMemoHandlR>();
    internal int CurrentGetsIndex;
}
