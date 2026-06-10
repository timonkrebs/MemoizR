using MemoizR.StructuredAsyncLock;

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

    // Monotonic node-id source: the stable per-context identity signals carry in causality
    // stamps (issue #39) and derived nodes key their per-source stamp maps by.
    private int nextNodeId;

    /** causality-stamp capture (issue #39): while a node evaluates, the stamps observed on its
    * tracked source reads accumulate here, keyed by the EVALUATING NODE rather than by flow.
    * Structured-concurrency children read on child flows/scopes but evaluate on behalf of the
    * owning node (their CurrentReaction), so per-flow storage would scatter one evaluation's
    * capture across scopes; keying by node collects it in one bucket. It also makes nested
    * evaluations naturally disjoint (an inner memo's reads record to the inner node's bucket
    * while its CurrentReaction is installed) with no push/pop, and the per-node mutex
    * (invariant I1) guarantees at most one open capture per node. All access under Lock; a
    * record against a node with no open bucket is dropped (e.g. a superseded race loser reading
    * after the winner already published and closed the bucket). */
    private readonly Dictionary<IMemoHandlR, Dictionary<int, CausalityStamp>> stampCaptures = new();

    internal int NextNodeId() => Interlocked.Increment(ref nextNodeId);

    internal void BeginStampCapture(IMemoHandlR node)
    {
        lock (Lock)
        {
            stampCaptures[node] = new();
        }
    }

    internal void RecordSourceStamp(IMemoHandlR? evaluatingNode, int sourceId, CausalityStamp stamp)
    {
        if (evaluatingNode == null)
        {
            return;
        }

        lock (Lock)
        {
            if (!stampCaptures.TryGetValue(evaluatingNode, out var bucket))
            {
                return;
            }

            // Re-reads of the same source within one evaluation join: the computed value may
            // have consumed both publications, and the join is their monotone upper bound.
            bucket[sourceId] = bucket.TryGetValue(sourceId, out var existing) ? existing.Join(stamp) : stamp;
        }
    }

    internal Dictionary<int, CausalityStamp> TakeStampCapture(IMemoHandlR node)
    {
        lock (Lock)
        {
            return stampCaptures.Remove(node, out var bucket) ? bucket : new();
        }
    }

    // Close a capture without consuming it -- the failure paths, where the node keeps its
    // previous stamp. A no-op when the bucket was already taken.
    internal void DiscardStampCapture(IMemoHandlR node)
    {
        lock (Lock)
        {
            stampCaptures.Remove(node);
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
            // Resolve the scope once: every ReactionScope access re-enters this very lock and
            // probes the dictionary, and this method runs on every tracked Get.
            var scope = ReactionScope;
            var noNewGets = scope.CurrentGets.Length == 0;

            var hasEnoughSources = scope.CurrentReaction?.Sources?.Length > 0 && scope.CurrentReaction.Sources.Length >= scope.CurrentGetsIndex + 1;
            var currentSourceEqualsThis = hasEnoughSources && scope.CurrentReaction!.Sources?[scope.CurrentGetsIndex] == memoHandlR;

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
    }

    // Pins a FRESH scope onto the current flow, replacing any inherited pin. Returns the scope;
    // the caller must keep it strongly referenced for the duration of its use (the registry holds
    // it only weakly).
    internal ReactionScope ForceNewScope()
    {
        return MintAndPinScope();
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
