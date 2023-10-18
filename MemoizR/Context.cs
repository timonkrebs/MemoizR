using MemoizR.StructuredAsyncLock;

namespace MemoizR;

public class Context
{
    internal AsyncAsymmetricLock ContextLock = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    internal IMemoHandlR? CurrentReaction = null;
    internal IMemoHandlR[] CurrentGets = Array.Empty<IMemoHandlR>();
    internal int CurrentGetsIndex;
    internal bool saveMode = true;

    internal void CheckDependenciesTheSame(IMemoHandlR memoHandlR)
    {
        var hasCurrentGets = CurrentGets.Length == 0;
        
        var hasEnoughSources = CurrentReaction?.Sources?.Length > 0 && CurrentReaction.Sources.Length >= CurrentGetsIndex + 1;
        var currentSourceEqualsThis = hasEnoughSources && CurrentReaction!.Sources?[CurrentGetsIndex] == memoHandlR;

        if (hasCurrentGets && currentSourceEqualsThis)
        {
            CurrentGetsIndex++;
        }
        else
        {
            CurrentGets = !CurrentGets.Any() 
                ? new[] { memoHandlR } 
                : CurrentGets.Union(new[] { memoHandlR }).ToArray();
        }
    }
}
