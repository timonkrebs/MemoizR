using MemoizR.StructuredAsyncLock;
using Nito.AsyncEx;

namespace MemoizR;

public class ReactionScope
{
    // CurrentReaction is read on the lock-free Get fast path (e.g. MemoizR.Get), so it stays
    // volatile for that read's visibility. CurrentGets/CurrentGetsIndex are volatile because no
    // single monitor covers all their accesses: writes go through CheckDependenciesTheSame under
    // Context.Lock, but StructuredReduceJob's parallel children (which share their parent flow's
    // scope -- only StructuredResultsJob forces per-child scopes) read them under the job's own
    // Lock, and the per-flow-reentrant ContextLock grants all those children concurrently, so it
    // serializes none of this. volatile supplies the missing release/acquire pairing (ADR 0001
    // rule 2); writes are whole-array swaps, so the reference publish is atomic.
    internal volatile IMemoHandlR? CurrentReaction = null;
    internal volatile IMemoHandlR[] CurrentGets = [];
    internal volatile int CurrentGetsIndex;
    internal AsyncAsymmetricLock ContextLock = new();
}

public class Context
{
    private Lock Lock { get; } = new();
    private static readonly AsyncLocal<double> AsyncLocalScope = new();

    /** current capture context for identifying sources (other memoizR elements)
    * - active while evaluating a memoizR function body
    * ConcurrentDictionary so the hot read paths resolve scopes lock-free; Lock guards only the
    * dependency-capture mutation (CheckDependenciesTheSame) and the evaluation refcount. */
    private readonly System.Collections.Concurrent.ConcurrentDictionary<double, WeakReference<ReactionScope>> AsyncReactionScopes = new();
    private int scopeRegistrationsSinceLastPrune;

    // Whether the current async flow has a pinned scope. An UNPINNED flow's scope would be
    // freshly minted, so its CurrentReaction is null by construction -- which lets the Get fast
    // paths skip scope resolution (and the mint!) entirely for clean reads.
    internal bool HasFlowScope => AsyncLocalScope.Value != 0;

    public ReactionScope ReactionScope
    {
        get
        {
            var key = AsyncLocalScope.Value;
            if (key == 0)
            {
                // No scope is pinned to this flow, so the scope could never be looked up again:
                // registering it would only leak a dictionary entry per access. Hand out a
                // throwaway.
                return new();
            }

            return GetScopeForKey(key);
        }
    }

    // Lock-free resolve-or-resurrect for a pinned key. The resurrection must be atomic: several
    // tasks can share one flow key concurrently (debounced reaction updates inherit the
    // triggering Set's flow), and two of them racing a dead entry must agree on ONE fresh scope
    // -- a last-write-wins overwrite would hand them different ContextLocks.
    private ReactionScope GetScopeForKey(double key)
    {
        if (AsyncReactionScopes.TryGetValue(key, out var reactionScopeRef)
            && reactionScopeRef.TryGetTarget(out var reactionScope))
        {
            return reactionScope;
        }

        ReactionScope fresh = new();
        WeakReference<ReactionScope> freshRef = new(fresh);
        while (true)
        {
            var existing = AsyncReactionScopes.GetOrAdd(key, freshRef);
            if (ReferenceEquals(existing, freshRef))
            {
                return fresh;
            }
            if (existing.TryGetTarget(out var live))
            {
                return live;
            }
            if (AsyncReactionScopes.TryUpdate(key, freshRef, existing))
            {
                return fresh;
            }
        }
    }

    public CancellationTokenSource? CancellationTokenSource { get; private set; }

    // The experimental actor engine's serial seat (ADR 0006): one per context, like the
    // ReactionScope machinery, so keyed factories share one actor. Lazy because most contexts
    // never create actor-engine nodes.
    private readonly Lazy<GraphActor> graphActor = new(() => new());
    internal GraphActor GraphActor => graphActor.Value;

    // The node-id slice this context allocates from: ids are handed out monotonically in
    // [IdRangeStart, IdRangeEnd). They are the stable per-context identity signals carry in
    // causality stamps (issue #39). Distributed peers carve the shared 31-bit id space into
    // DISJOINT contiguous slices so stamps merged across peers can never collide on an id (and
    // each peer occupies its own subtree of the interval encoding, keeping merged stamps
    // compact); exhausting a slice throws rather than silently bleeding into a neighbour's ids.
    internal int IdRangeStart { get; }
    internal int IdRangeEnd { get; }
    private int nextNodeId;

    // The incarnation epoch every causality stamp of this context carries: ids and triggers
    // restart when a process (and so its context) restarts, so a recreated graph reissues
    // (id, trigger) pairs that already escaped over the wire -- the random nonzero epoch is
    // what keeps pre- and post-reset observations from ever being confused (see
    // CausalityStamp). Within a living context a "reset" node is simply a new node, with a
    // fresh id that was never handed out before.
    internal long Epoch { get; } = Random.Shared.NextInt64(1, long.MaxValue);

    /** causality-stamp capture (issue #39): while a node evaluates, the stamps observed on its
    * tracked source reads accumulate here, keyed by the EVALUATING NODE rather than by flow:
    * structured-concurrency children read on child flows/scopes but evaluate on behalf of the
    * owning node (their CurrentReaction), and nested evaluations stay naturally disjoint with
    * no push/pop (the per-node mutex guarantees at most one open capture per node). All access
    * under Lock; a record against a node with no open bucket is dropped (e.g. a superseded race
    * loser reading after the winner already published and closed the bucket). */
    private readonly Dictionary<IMemoHandlR, StampCapture> stampCaptures = new();

    internal Context(int idRangeStart = 1, int idRangeEnd = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(idRangeStart);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idRangeEnd, idRangeStart);
        IdRangeStart = idRangeStart;
        IdRangeEnd = idRangeEnd;
        nextNodeId = idRangeStart - 1;
    }

    internal int NextNodeId()
    {
        var id = Interlocked.Increment(ref nextNodeId);
        // The lower-bound check also catches int wrap-around past int.MaxValue.
        if (id < IdRangeStart || id >= IdRangeEnd)
        {
            throw new InvalidOperationException(
                $"The context exhausted its node-id slice [{IdRangeStart}, {IdRangeEnd}). Distributed peers must be provisioned with slices sized for their graphs.");
        }
        return id;
    }

    internal void BeginStampCapture(IMemoHandlR node, bool branchAware = false)
    {
        lock (Lock)
        {
            stampCaptures[node] = new(branchAware);
        }
    }

    // Marks the evaluating node's open capture unverifiable for the ambient branch: used when a
    // tracked read observed a source whose own evidence is unverifiable, or when a tracked read
    // faulted (the caller may catch the fault and publish a fallback -- evidence omitting the
    // faulted source would hide a real control-flow dependency).
    internal void MarkStampCaptureUnverifiable(IMemoHandlR? evaluatingNode)
    {
        if (evaluatingNode == null)
        {
            return;
        }

        var branch = RaceBranchFlow.Current.Value;
        lock (Lock)
        {
            if (stampCaptures.TryGetValue(evaluatingNode, out var capture))
            {
                capture.Poison(branch);
            }
        }
    }

    internal void RecordSourceStamp(IMemoHandlR? evaluatingNode, int sourceId, CausalityStamp stamp)
    {
        if (evaluatingNode == null)
        {
            return;
        }

        // Resolve the ambient racing-branch tag outside the monitor (an AsyncLocal read); 0 for
        // every read that is not inside a racing branch.
        var branch = RaceBranchFlow.Current.Value;
        lock (Lock)
        {
            RecordSourceStampCore(evaluatingNode, sourceId, stamp, branch);
        }
    }

    // Must be called under Lock.
    private void RecordSourceStampCore(IMemoHandlR? evaluatingNode, int sourceId, CausalityStamp stamp, int branch)
    {
        if (evaluatingNode == null)
        {
            return;
        }

        if (stampCaptures.TryGetValue(evaluatingNode, out var capture))
        {
            capture.Record(sourceId, stamp, branch);
        }
    }

    // The tracked read of an up-to-date node, fused: dependency registration, the single box
    // read, and the stamp record run under ONE Lock acquisition -- the register and the record
    // each took their own before, doubling context-wide lock traffic on the hottest tracked
    // path. The register-then-read order is preserved: the eager observer subscription must be
    // in place before the value is read, or a Set landing in between would notify nobody. The
    // scope is the caller's already-resolved (and strongly rooted) instance, so no registry
    // probe here. A leaf signal's own evidence is never unverifiable (ForOwnStamp); a clean
    // memo's can be (contagion from a poisoned source), and unverifiability must poison the
    // caller's capture instead of contributing a stamp (see MemoBase.ReadWithEvidence).
    internal (T Value, StampEvidence Evidence) TrackedRead<T>(MemoHandlR<T> source, ReactionScope scope)
    {
        var branch = RaceBranchFlow.Current.Value;
        lock (Lock)
        {
            CheckDependenciesCore(scope, source);
            var pair = source.ValueAndEvidence;
            if (pair.Evidence.Unverifiable)
            {
                if (scope.CurrentReaction is { } reaction && stampCaptures.TryGetValue(reaction, out var capture))
                {
                    capture.Poison(branch);
                }
            }
            else
            {
                RecordSourceStampCore(scope.CurrentReaction, source.Id, pair.Evidence.Stamp, branch);
            }
            return pair;
        }
    }

    // Closes and returns the node's capture (a shared empty one when none is open). The failure
    // paths call this too, discarding the result -- the node then keeps its previous stamp.
    internal StampCapture TakeStampCapture(IMemoHandlR node)
    {
        lock (Lock)
        {
            return stampCaptures.Remove(node, out var capture) ? capture : StampCapture.Empty;
        }
    }

    private int evaluationDepth;

    // The context-wide CancellationTokenSource is shared by every evaluation in flight (so
    // Cancel() reaches the whole computation tree), so its lifetime must be refcounted: it is
    // created by the first root evaluation to enter and torn down by the LAST one to exit.
    // The previous protocol ("the call that created it nulls it in its finally") lost the source
    // under concurrency: root A creates it, root B enters while it exists, A finishes and nulls
    // the shared field while B is still mid-evaluation -- B's later reads then NRE'd and
    // Cancel() silently stopped working.
    //
    // Cancellation deliberately reaches every evaluation OVERLAPPING the canceled one: an enter
    // while the (possibly canceled) source exists JOINS that tree -- this is what lets Cancel()
    // abort a debounced reaction update whose nested parent scans re-enter here
    // (TestMultipleMapHandlingCancel pins it). The flip side is accepted and bounded: after a
    // Cancel(), evaluations that keep overlapping the canceled tree keep failing with
    // TaskCanceledException until the refcount reaches zero once; the very next root evaluation
    // then gets a fresh source (Exit nulls the field at depth zero, under this same lock, so a
    // canceled source can never outlive its tree).
    internal void EnterEvaluationScope()
    {
        lock (Lock)
        {
            evaluationDepth++;
            CancellationTokenSource ??= new();
        }
    }

    internal void ExitEvaluationScope()
    {
        lock (Lock)
        {
            if (--evaluationDepth == 0)
            {
                CancellationTokenSource = null;
            }
        }
    }

    // The pull-driven evaluation-lock scaffold, factored out of the per-entry-point copies in
    // MemoizR<T>, the concurrent nodes, ConcurrentRace, and ReactionBase. It resolves-or-pins the
    // ambient scope and keeps it strongly rooted for the whole evaluation (a collected scope would
    // resurrect under the same key with a FRESH ContextLock -- not the one this evaluation holds),
    // takes the per-node mutex (one evaluation of a node at a time, across awaits) and then this
    // flow's upgradeable ContextLock, and refcounts the evaluation so the shared
    // CancellationTokenSource lives exactly as long as the evaluation tree (created at depth 0,
    // torn down when the last overlapping evaluation exits). Used only by the entry points that
    // resolve-or-pin the ambient scope via GetOrCreateScope; the reaction's debounced/Resume paths
    // own or conditionally tear down a scope of their own, so they keep their bespoke wrappers.
    internal async Task<TResult> EvaluateUnderLockAsync<TResult>(AsyncLock mutex, Func<Task<TResult>> evaluate)
    {
        var scope = GetOrCreateScope();
        try
        {
            using (await mutex.LockAsync())
            using (await scope.ContextLock.UpgradeableLockAsync())
            {
                EnterEvaluationScope();
                try
                {
                    return await evaluate();
                }
                finally
                {
                    ExitEvaluationScope();
                }
            }
        }
        finally
        {
            GC.KeepAlive(scope);
        }
    }

    // Void-body convenience over EvaluateUnderLockAsync for the IMemoizR.UpdateIfNecessary fan-in,
    // which recomputes for its effect and has no value to return.
    internal Task UpdateUnderLockAsync(AsyncLock mutex, Func<Task> update)
        => EvaluateUnderLockAsync(mutex, async () =>
        {
            await update();
            return true;
        });

    // Pins a scope to the current async flow if it has none yet. Returns whether a scope was
    // created: a caller that pairs this with CleanScope must only clean up when it created the
    // scope, otherwise it destroys a live scope an enclosing evaluation on the same flow is
    // still using (its dependency capture would silently resolve to a fresh empty scope).
    internal bool CreateNewScopeIfNeeded()
    {
        if (AsyncLocalScope.Value != 0)
        {
            return false;
        }
        MintAndPinScope();
        return true;
    }

    // Pins a scope if needed and returns it. Used by the Get slow paths; the resolution is
    // lock-free (ConcurrentDictionary), so this is cheap even under cross-flow contention.
    internal ReactionScope GetOrCreateScope()
    {
        var key = AsyncLocalScope.Value;
        if (key == 0)
        {
            return MintAndPinScope();
        }

        return GetScopeForKey(key);
    }

    // The one mint-and-pin path: a fresh random key on the AsyncLocal plus a registry entry.
    // Callers MUST keep the returned scope strongly referenced for as long as they rely on its
    // identity (the registry holds it only weakly; see ReactionScope resurrection).
    private ReactionScope MintAndPinScope()
    {
        var key = Random.Shared.NextDouble();
        AsyncLocalScope.Value = key;
        ReactionScope created = new();
        RegisterScope(key, created);
        return created;
    }

    private void RegisterScope(double key, ReactionScope scope)
    {
        AsyncReactionScopes.TryAdd(key, new(scope)); // fresh random key: cannot collide

        // Sweep policy: while the table is small (<= 64 entries) sweep on every registration --
        // an O(64)-bounded scan, so dead flows are pruned promptly. Past that, sweep only when
        // the registrations since the last sweep rival the table size: pruning a LARGE table on
        // every mint is O(table) per registration (quadratic under sustained traffic), while the
        // rivalry condition keeps it O(1) amortized. Concurrent double-sweeps are harmless.
        var registrations = Interlocked.Increment(ref scopeRegistrationsSinceLastPrune);
        var count = AsyncReactionScopes.Count;
        if (count <= 64 || registrations >= count / 2)
        {
            Interlocked.Exchange(ref scopeRegistrationsSinceLastPrune, 0);
            PruneDeadScopes();
        }
    }

    // Test hook: the number of registered scope entries (live or dead), for asserting that
    // PruneDeadScopes keeps the registry bounded.
    internal int RegisteredScopeCount => AsyncReactionScopes.Count;

    // The scope targets are weak, but the dictionary entries themselves are not: sweep the dead
    // ones so the map stays bounded by the number of LIVE flows. The conditional TryRemove only
    // removes the exact dead entry, so it can never race away a concurrently resurrected scope.
    internal void PruneDeadScopes()
    {
        foreach (var kvp in AsyncReactionScopes)
        {
            if (!kvp.Value.TryGetTarget(out _))
            {
                // Conditional remove (key AND value must match), so it can never race away a
                // concurrently resurrected scope.
                AsyncReactionScopes.TryRemove(kvp);
            }
        }
    }

    internal void CleanScope()
    {
        AsyncReactionScopes.TryRemove(AsyncLocalScope.Value, out _);
    }

    internal void CheckDependenciesTheSame(IMemoHandlR memoHandlR)
    {
        lock (Lock)
        {
            // Resolve the scope once: every ReactionScope access probes the registry, and this
            // method runs on every tracked Get.
            CheckDependenciesCore(ReactionScope, memoHandlR);
        }
    }

    // Must be called under Lock.
    private static void CheckDependenciesCore(ReactionScope scope, IMemoHandlR memoHandlR)
    {
        var noNewGets = scope.CurrentGets.Length == 0;

        // Sources is never null (initialized to [] and only ever swapped whole).
        var sources = scope.CurrentReaction?.Sources;
        var currentSourceEqualsThis = sources != null && sources.Length > scope.CurrentGetsIndex
            && sources[scope.CurrentGetsIndex] == memoHandlR;

        if (noNewGets && currentSourceEqualsThis)
        {
            Interlocked.Increment(ref scope.CurrentGetsIndex);
        }
        else
        {
            scope.CurrentGets = [.. scope.CurrentGets, memoHandlR];

            // Subscribe EAGERLY, at capture time, not after the evaluation completes: a Set
            // landing between this read and the deferred link rewiring would otherwise see no
            // observer and notify nobody -- the node would commit a value computed from the
            // pre-Set read with no Stale ever bumping its generation (the first-evaluation
            // subscription window). With the link in place immediately, that Set reaches the
            // node mid-evaluation, the commit is refused, and the normal machinery re-runs.
            // (Prefix-matched re-reads above are already subscribed from the previous run.)
            if (scope.CurrentReaction is IMemoizR reaction)
            {
                memoHandlR.AddObserver(reaction);
            }
        }
    }

    // Pins a FRESH scope onto the current flow, replacing any inherited pin. Returns the scope;
    // the caller must keep it strongly referenced for the duration of its use (the registry holds
    // it only weakly).
    internal ReactionScope ForceNewScope()
    {
        return MintAndPinScope();
    }

    /// <summary>
    /// Whether the current async flow is inside a MemoizR-serialized graph evaluation, i.e. it
    /// holds its flow's evaluation lock (in either mode). A flow with no pinned scope cannot hold
    /// a stable ContextLock, so it reads as not isolated -- short-circuited here so the common
    /// "outside evaluation" case does not allocate the throwaway scope the ReactionScope getter
    /// would otherwise mint. Point-in-time: only meaningful as "I am inside the locked region",
    /// never as a reason to skip acquiring the lock.
    /// </summary>
    public bool IsEvaluationIsolated => AsyncLocalScope.Value != 0 && ReactionScope.ContextLock.IsHeldByCurrentFlow;

    // A lock-engine computation is actively CAPTURING dependencies on this flow (a memo
    // recompute or a reaction run; null during Set callbacks and inside Untrack). Read by the
    // actor engine to reject cross-engine reads that would silently escape capture; the
    // HasFlowScope short-circuit keeps unpinned flows from minting a throwaway scope.
    internal bool IsComputationCapturing => HasFlowScope && ReactionScope.CurrentReaction is not null;

    /// <summary>
    /// Dynamic isolation check (issue #36), the runtime analog of Swift's
    /// <c>preconditionIsolated()</c>: throws when the current async flow is not inside a
    /// MemoizR-serialized graph evaluation.
    /// </summary>
    public void AssertEvaluationIsolated()
    {
        if (!IsEvaluationIsolated)
        {
            throw new InvalidOperationException(
                "This code expected to run inside a MemoizR graph evaluation (a Get/Set/recompute or " +
                "reaction update holding the current flow's evaluation lock), but no evaluation is active " +
                "on this async flow.");
        }
    }

    public T Untrack<T>(Func<T> fn)
    {
        if (!HasFlowScope)
        {
            // An unpinned flow has no capturing reaction by construction; resolving the getter
            // would only mint throwaway scopes whose CurrentReaction writes are dead stores.
            return fn();
        }

        // Resolve ONCE: repeated getter access can observe different instances (the weakly-held
        // scope can be collected and resurrected between accesses), in which case the restore
        // below would land on a different scope than the one that was nulled.
        var scope = ReactionScope;
        var listener = scope.CurrentReaction;
        scope.CurrentReaction = null;
        try
        {
            return fn();
        }
        finally
        {
            scope.CurrentReaction = listener;
        }
    }

    public async Task<T> Untrack<T>(Func<Task<T>> fn)
    {
        if (!HasFlowScope)
        {
            return await fn();
        }

        var scope = ReactionScope;
        var listener = scope.CurrentReaction;
        scope.CurrentReaction = null;
        try
        {
            return await fn();
        }
        finally
        {
            scope.CurrentReaction = listener;
        }
    }
}

// One evaluation's observed source stamps (issue #39), keyed by source id. All mutation happens
// under Context.Lock. The capture is POISONED when the same source is re-read across DIFFERENT
// publications: the computed value then mixes two versions of one write history, so no single
// per-source stamp is honest -- not the older (a consumer comparing against the newer
// ingredient would see false agreement) and not the newer (the over-claim the capture
// discipline forbids). A poisoned capture publishes no evidence at all (the empty stamp claims
// nothing, which is always safe); the mid-evaluation Set that caused the mix also bumped the
// node's generation, so the Clean commit is refused and the next recompute publishes clean
// evidence.
// The ambient tag of the racing branch currently evaluating on this flow (0 = not inside a
// racing branch: ordinary evaluations and a race's shared action). Set by StructuredRaceJob at
// the top of each branch body, so the tag flows into everything the branch spawns; the mutation
// lives in the branch task's own ExecutionContext and never leaks to the parent flow. It lets
// one race capture attribute entries per branch WITHOUT touching CurrentReaction -- which must
// stay the race itself, because capture-time observer wiring hangs off it.
internal static class RaceBranchFlow
{
    internal static readonly AsyncLocal<int> Current = new();
}

// One evaluation's observed source stamps (issue #39), keyed by (source id, racing branch).
// All mutation happens under Context.Lock. Only a RACE's capture is branch-aware: an ordinary
// node evaluated inside a racing branch opens its own capture, and the ambient branch tag of
// the enclosing race must not leak into it (its Seal(0) would discard everything) -- so a
// non-branch-aware capture files every record under branch 0.
//
// A branch is POISONED -- its evidence is unverifiable -- when the same source is re-read by
// that branch across DIFFERENT publications (the computed value then mixes two versions of one
// write history, so no single per-source stamp is honest: not the older, which would show
// false agreement with the newer ingredient, and not the newer, which is the forbidden
// over-claim), or when a read observed a source whose own evidence is unverifiable, or when a
// tracked read faulted (the caller may catch and publish a fallback whose control flow the
// missing entry would hide). Poison is tracked PER BRANCH so a losing racer's mixed re-read
// cannot destroy the evidence of a clean winner; Seal treats it as fatal only for branch 0 and
// the winning branch. The same source read differently by DIFFERENT branches is not poison --
// branches are alternative computations, resolved by the seal.
internal sealed class StampCapture
{
    // Returned by TakeStampCapture when no capture is open; never registered, so never mutated.
    internal static readonly StampCapture Empty = new(false);

    private readonly bool branchAware;
    private readonly Dictionary<(int Source, int Branch), CausalityStamp> entries = new();
    private readonly HashSet<int> poisonedBranches = new();

    public StampCapture(bool branchAware)
    {
        this.branchAware = branchAware;
    }

    public void Poison(int branch) => poisonedBranches.Add(branchAware ? branch : 0);

    public void Record(int sourceId, CausalityStamp stamp, int branch)
    {
        var key = (sourceId, branchAware ? branch : 0);
        if (entries.TryGetValue(key, out var existing))
        {
            if (!existing.Equals(stamp))
            {
                Poison(branch);
            }
            return;
        }

        entries[key] = stamp;
    }

    // Reduce the branch-tagged entries to one stamp per source, keeping only the evidence that
    // fed the published value: branch 0 (the owning evaluation itself, or a race's shared
    // action) plus the winning branch. For every non-race capture all entries carry branch 0,
    // so Seal(0) keeps everything. A disagreement between the shared and the winning entry for
    // one source is the same mixed-publication situation as a poisoned re-read.
    public (bool Poisoned, Dictionary<int, CausalityStamp> Stamps) Seal(int winningBranch)
    {
        var stamps = new Dictionary<int, CausalityStamp>();
        if (poisonedBranches.Contains(0) || poisonedBranches.Contains(winningBranch))
        {
            return (true, stamps);
        }

        foreach (var ((source, branch), stamp) in entries)
        {
            if (branch != 0 && branch != winningBranch)
            {
                continue;
            }
            if (stamps.TryGetValue(source, out var existing))
            {
                if (!existing.Equals(stamp))
                {
                    return (true, stamps);
                }
                continue;
            }
            stamps[source] = stamp;
        }
        return (false, stamps);
    }
}
