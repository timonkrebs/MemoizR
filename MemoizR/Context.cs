namespace MemoizR;

public class Context
{
    /** current capture context for identifying @reactive sources (other reactive elements) and cleanups
    * - active while evaluating a reactive function body  */
    internal dynamic? CurrentReaction = null;  // ToDo type it to get rid of dynamic type but without forcing boxing
    internal IMemoHandlR[] CurrentGets = Array.Empty<IMemoHandlR>();
    internal int CurrentGetsIndex = 0;
}
