using MemoizR.StructuredAsyncLock;

namespace MemoizR;

public class Context
{
    internal AsyncAsymmetricLock contextLock = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body  */
    internal IMemoHandlR? CurrentReaction = null;
    internal IMemoHandlR[] CurrentGets = Array.Empty<IMemoHandlR>();
    internal int CurrentGetsIndex;

    internal void CheckDependenciesTheSame(IMemoHandlR memoHandlR)
    {
        var hasCurrentGets = CurrentGets == null || CurrentGets.Length == 0;
        
        var hasEnoughSources = CurrentReaction?.Sources?.Length > 0 && CurrentReaction.Sources.Length >= CurrentGetsIndex + 1;
        var currentSourceEqualsThis = hasEnoughSources && CurrentReaction!.Sources[CurrentGetsIndex] == memoHandlR;

        if (hasCurrentGets && currentSourceEqualsThis)
        {
            CurrentGetsIndex++;
        }
        else
        {
            if (!CurrentGets!.Any()) CurrentGets = new[] { memoHandlR };
            else CurrentGets = CurrentGets!.Union(new[] { memoHandlR }).ToArray();
        }
    }
}
