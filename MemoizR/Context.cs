using MemoizR.AsyncLock;

namespace MemoizR;

public class Context
{
    internal AsyncPriorityLock contextLock = new();
    internal AsyncLocal<int> asyncLocalScope = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    internal IMemoHandlR? CurrentReaction = null;
    internal IMemoHandlR[] CurrentGets = Array.Empty<IMemoHandlR>();
    internal int CurrentGetsIndex;
}
