using MemoizR.AsyncLock;

namespace MemoizR;

public class Context
{
    internal AsyncPriorityLock contextLock = new AsyncPriorityLock();
    internal int reactionIndex = 0;

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    internal IMemoHandlR? CurrentReaction = null;
    internal IMemoHandlR[] CurrentGets = Array.Empty<IMemoHandlR>();
    internal int CurrentGetsIndex;
}
