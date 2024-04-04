using MemoizR.StructuredAsyncLock;
using Nito.AsyncEx;

namespace MemoizR;

public class Context
{
    internal AsyncAsymmetricLock ContextLock = new();
    internal AsyncLock Mutex = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    internal volatile IMemoHandlR? CurrentReaction = null;
    internal volatile IMemoHandlR[] CurrentGets = [];
    internal volatile int CurrentGetsIndex;

    internal void CheckDependenciesTheSame(IMemoHandlR memoHandlR)
    {
        lock (this)
        {
            var hasCurrentGets = CurrentGets.Length == 0;

            var hasEnoughSources = CurrentReaction?.Sources?.Length > 0 && CurrentReaction.Sources.Length >= CurrentGetsIndex + 1;
            var currentSourceEqualsThis = hasEnoughSources && CurrentReaction!.Sources?[CurrentGetsIndex] == memoHandlR;

            if (hasCurrentGets && currentSourceEqualsThis)
            {
                Interlocked.Increment(ref CurrentGetsIndex);
            }
            else
            {
                CurrentGets = !CurrentGets.Any()
                    ? [memoHandlR] 
                    : [..CurrentGets,  memoHandlR];
            }
        }
    }
}
