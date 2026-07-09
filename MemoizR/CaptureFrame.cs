namespace MemoizR;

// The flow-state installation every lock-engine evaluation performs around its computation:
// install the node as the scope's capturing reaction with a fresh dependency-capture window,
// mark this context as the flow's ambient evaluating context (the cross-engine guard reads it),
// and open the node's stamp-capture bucket. Restore undoes all of it and drops a capture left
// open by the failure paths (the node keeps its previous stamp; a no-op after a successful
// publish). Shared by MemoBase.Update, ReactionBase.Update and ConcurrentRace.Update --
// previously three hand-kept copies that had to be edited in lockstep for every hardening, the
// same reason the recompute protocol itself lives in MemoBase.
//
// Restore is called from the evaluation's finally, deliberately NOT via using/Dispose: the code
// between the computation and the method's end (value comparison, diamond marking, the trailing
// commit) must run with the previous state already restored -- a user Equals that performed a
// tracked read would otherwise capture into this node's dead window -- and the catch paths must
// still observe the failed run's CurrentGets before the restore.
internal readonly struct CaptureFrame
{
    private readonly Context context;
    private readonly ReactionScope scope;
    private readonly IMemoHandlR node;
    private readonly IMemoHandlR? prevReaction;
    private readonly IMemoHandlR[] prevGets;
    private readonly int prevGetsIndex;
    private readonly Context? prevAmbientContext;
    private readonly bool branchAware;
    private readonly int prevBranch;

    private CaptureFrame(Context context, ReactionScope scope, IMemoHandlR node, bool branchAware)
    {
        this.context = context;
        this.scope = scope;
        this.node = node;
        this.branchAware = branchAware;

        prevReaction = scope.CurrentReaction;
        prevGets = scope.CurrentGets;
        prevGetsIndex = scope.CurrentGetsIndex;
        prevAmbientContext = LockEngineFlow.EvaluatingContext.Value;
        prevBranch = RaceBranchFlow.Current.Value;

        scope.CurrentReaction = node;
        scope.CurrentGets = [];
        scope.CurrentGetsIndex = 0;
        LockEngineFlow.EvaluatingContext.Value = context;
        if (branchAware)
        {
            // A race's own frame: its shared action must record as branch 0 of THIS capture,
            // not under an enclosing race's branch id (see ConcurrentRace.Update).
            RaceBranchFlow.Current.Value = 0;
        }
        context.BeginStampCapture(node, branchAware);
    }

    internal static CaptureFrame Install(Context context, ReactionScope scope, IMemoHandlR node, bool branchAware = false)
    {
        return new(context, scope, node, branchAware);
    }

    internal void Restore()
    {
        context.TakeStampCapture(node);
        scope.CurrentGets = prevGets;
        scope.CurrentReaction = prevReaction;
        scope.CurrentGetsIndex = prevGetsIndex;
        LockEngineFlow.EvaluatingContext.Value = prevAmbientContext;
        if (branchAware)
        {
            RaceBranchFlow.Current.Value = prevBranch;
        }
    }
}
