using Nito.AsyncEx;

namespace MemoizR;

public abstract class SignalHandlR : IMemoHandlR
{
    private Lock Lock { get; } = new();
    internal IMemoHandlR[] Sources { get; set; } = []; // sources in reference order, not deduplicated (up links)
    internal WeakReference<IMemoizR>[] Observers { get; set; } = []; // nodes that have us as sources (down links)

    internal Context Context;

    protected AsyncLock mutex = new();

    IMemoHandlR[] IMemoHandlR.Sources
    {
        get => Sources;
        set
        {
            lock (Lock)
            {
                Sources = value;
            }
        }
    }
    WeakReference<IMemoizR>[] IMemoHandlR.Observers
    {
        get => Observers;
        set
        {
            lock (Lock)
            {
                Observers = value;
            }
        }
    }

    // Cache-state cell for every node type that participates in invalidation (memos, the
    // concurrent nodes, reactions); see CacheStateCell for the generation-guard protocol. Plain
    // signals never touch it (their writes are Sets under the ContextLock; they have no cached
    // recompute state), they just carry the tiny unused cell so the protocol lives in one place.
    internal readonly CacheStateCell stateCell = new(CacheState.CacheClean);

    /// <summary>
    /// Stable per-context identity for causality stamps (issue #39): signals appear in stamps
    /// under this id, and derived nodes key their per-source stamp map by it.
    /// </summary>
    public int Id { get; }

    // Backs Evidence for value-less nodes (reactions); value nodes override Evidence to read it
    // from the same volatile box as the value.
    private volatile StampEvidence evidence = StampEvidence.None;

    /// <summary>
    /// The published causality evidence of the last completed evaluation, as ONE immutable
    /// snapshot: the node's own stamp, the per-source map it was sealed from, and whether the
    /// evaluation was unverifiable. Read this when you need the fields to describe the same
    /// publication -- the convenience accessors below each take their own snapshot, so two
    /// separate calls can straddle a concurrent recompute.
    /// </summary>
    public virtual StampEvidence Evidence => evidence;

    public CausalityStamp Stamp => Evidence.Stamp;

    /// <summary>
    /// "Every Node keeps a Stamp for each of its Sources" (issue #39): the stamp observed on
    /// each tracked source read of the last completed evaluation, keyed by source id -- the
    /// data a distributed sync layer exchanges. Signals keep the empty map.
    /// </summary>
    public IReadOnlyDictionary<int, CausalityStamp> SourceStamps => Evidence.SourceStamps;

    // Publish the evidence captured during a completed evaluation of a VALUE-LESS node
    // (reactions). Value nodes publish through MemoHandlR.PublishValueWithStamps instead, which
    // puts the evidence in the value box.
    internal void PublishCapturedStamps()
    {
        evidence = StampEvidence.FromCapture(Context.TakeStampCapture(this));
    }

    public string Label { get; init; } = "Label";

    internal SignalHandlR(Context context)
    {
        this.Context = context;
        this.Id = context.NextNodeId();
    }

    // Atomic add-if-absent on our observer down-links. The membership check and the array swap
    // must happen under one monitor: observer mutations arrive from three different lock domains
    // (capture under Context.Lock, rewiring under a flow's ContextLock + node mutex, job
    // accumulation under the job Lock), and two unsynchronized read-modify-writes of the same
    // array lose one of the entries -- a silently dropped subscription, i.e. a missed-trigger.
    void IMemoHandlR.AddObserver(IMemoizR observer) => AddObserver(observer);

    internal void AddObserver(IMemoizR observer)
    {
        lock (Lock)
        {
            foreach (var existing in Observers)
            {
                if (existing.TryGetTarget(out var o) && ReferenceEquals(o, observer))
                {
                    return;
                }
            }
            Observers = [.. Observers, new(observer)];
        }
    }

    void IMemoHandlR.RemoveObserver(IMemoizR observer) => RemoveObserver(observer);

    internal void RemoveObserver(IMemoizR observer)
    {
        lock (Lock)
        {
            // Dead weak references are swept opportunistically while we are rebuilding anyway.
            Observers = [.. Observers.Where(x => x.TryGetTarget(out var o) && !ReferenceEquals(o, observer))];
        }
    }

    // Rewire our source up-links and the parents' observer down-links to match the sources
    // captured during the current evaluation (Context.ReactionScope.CurrentGets). Must only be
    // called by IMemoizR nodes, inside their ContextLock-serialized evaluation.
    //
    // Diff-based on purpose: only sources DROPPED by this run lose their down-link to us.
    // The previous strip-everything-then-re-add left a window in which a retained source had no
    // observer entry for us -- a Set landing there notified nobody and never bumped our
    // generation, so the value committed at the end of the evaluation cached stale forever.
    internal void UpdateSourceAndObserverLinks()
    {
        AssertRewiringIsolated();
        var self = (IMemoizR)this;
        // Resolve the scope once: every Context.ReactionScope access takes the context-wide lock
        // plus a dictionary probe, and this method reads it many times per recompute.
        var scope = Context.ReactionScope;

        IMemoHandlR[] newSources;
        if (scope.CurrentGets.Length > 0)
        {
            newSources = Sources.Length > 0 && scope.CurrentGetsIndex > 0
                ? [.. Sources.AsSpan(0, scope.CurrentGetsIndex), .. scope.CurrentGets]
                : scope.CurrentGets;
        }
        else if (Sources.Length > 0 && scope.CurrentGetsIndex < Sources.Length)
        {
            newSources = [.. Sources.AsSpan(0, scope.CurrentGetsIndex)];
        }
        else
        {
            return; // dependency set unchanged
        }

        foreach (var old in Sources)
        {
            if (!newSources.Contains(old))
            {
                old.RemoveObserver(self);
            }
        }
        Sources = newSources;
        foreach (var source in newSources)
        {
            // Usually a no-op: capture-time eager subscription already wired the link.
            source.AddObserver(self);
        }
    }

    // Dynamic isolation check (DEBUG only, issue #36): mechanically pins the "must only be
    // called inside a ContextLock-serialized evaluation" contract above, so a future caller that
    // reaches the rewiring without the lock fails loudly in every Debug test run instead of
    // corrupting the links silently. Not asserted in RemoveParentObservers: ReactionBase.Dispose
    // legitimately prunes links outside any evaluation.
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertRewiringIsolated()
    {
        Context.AssertEvaluationIsolated();
    }

    internal void RemoveParentObservers()
    {
        var self = (IMemoizR)this;
        foreach (var source in Sources)
        {
            source.RemoveObserver(self);
        }
    }

    // The diamond down-link: after this node recomputed to a changed value, mark our observers
    // dirty so they re-evaluate. Goes through the IMemoizR.State setter (InvalidateFromParent),
    // which absorbs the mark during the observer's own same-flow evaluation -- the observer is
    // reading this very node -- instead of bumping its generation. Iterating an empty Observers
    // array is a no-op, so the caller only needs its value-changed guard.
    internal void MarkObserversDirty()
    {
        foreach (var observer in Observers)
        {
            if (observer.TryGetTarget(out var o))
            {
                o.State = CacheState.CacheDirty;
            }
        }
    }

    // Escalate this node's dirtiness (a Stale) and, if the state escalated, propagate CacheCheck
    // to our observers. The generation is bumped even when the state was already at least this
    // dirty (see CacheStateCell.Invalidate); propagation is skipped then because the observers
    // were already notified when this node first reached that state -- an observer that commits
    // Clean inside the race window is re-notified by CommitCleanOrRenotifyAsync instead.
    // Non-async on purpose: the suppressed case is the common one under write storms and should
    // not pay for an async state machine.
    internal Task InvalidateAndPropagateAsync(CacheState state)
    {
        if (!stateCell.Invalidate(state))
        {
            return Task.CompletedTask;
        }

        return PropagateStaleToObserversAsync();
    }

    internal async Task PropagateStaleToObserversAsync(CacheState state = CacheState.CacheCheck)
    {
        foreach (var observer in Observers)
        {
            if (observer.TryGetTarget(out var o))
            {
                await o.Stale(state);
            }
        }
    }

    // Commit Clean against the snapshotted generation. If the commit is refused because an
    // invalidation landed mid-evaluation, this node stays dirty for its next Get/update -- but an
    // observer may have committed Clean against our pre-invalidation value in the same window
    // (the invalidation cascade stops at already-dirty nodes, so it can have missed the observer
    // entirely), so re-notify the observers; for a reaction observer this also re-schedules its
    // debounced update. Without this, a node whose commit lost the race could leave a descendant
    // cached-stale with nothing ever re-dirtying it.
    // Non-async, with a lock-free pre-check: if the state is already Clean, either this very
    // token's early commit succeeded (every invalidation escalates the state away from Clean, so
    // Clean here implies an unchanged generation) or a newer evaluation committed -- in both
    // cases there is nothing to do, and the common recompute path skips the gate entirely.
    internal Task CommitCleanOrRenotifyAsync(int token)
    {
        if (stateCell.State == CacheState.CacheClean || stateCell.TryCommitClean(token))
        {
            return Task.CompletedTask;
        }

        return PropagateStaleToObserversAsync();
    }

    // The CacheCheck parent scan shared by MemoBase.UpdateIfNecessary and
    // ReactionBase.UpdateIfNecessary: re-check each source that is itself a node, in order. A
    // source that recomputes to a CHANGED value marks THIS node dirty through the diamond
    // down-link, at which point we stop -- our computation may no longer use the remaining sources,
    // so we must not update a source we used last time but now don't. Returns whether any parent's
    // recompute FAULTED; the caller then stays un-committed so a later pass retries it. Faults are
    // suppressed here (not thrown) so one bad parent does not abort the scan of the others. Only
    // the scan is shared -- the commit that follows differs per node type, so it stays at the call
    // site.
    internal async Task<bool> ScanParentsForDirty()
    {
        var parentFaulted = false;
        foreach (var source in Sources)
        {
            if (source is IMemoizR memoizR)
            {
                var update = memoizR.UpdateIfNecessary(); // UpdateIfNecessary can change our state
                await update.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                parentFaulted |= update.IsFaulted;
            }

            if (stateCell.State == CacheState.CacheDirty)
            {
                break;
            }
        }
        return parentFaulted;
    }
}

public abstract class MemoHandlR<T> : SignalHandlR
{
    // Value is read on the lock-free Get fast path while another flow may be writing it under the
    // ContextLock. A generic T can be neither marked `volatile` nor read with Volatile.Read (the
    // generic overload is class-constrained), and a large struct T can tear under a concurrent
    // write. So the value is published through an immutable box held in a single volatile
    // reference: a write swaps in a fully-constructed box (an atomic reference store with release
    // semantics), and a read takes the reference once and returns its readonly fields -- always a
    // complete, untorn value. Every Update writes the box before setting State = CacheClean (a
    // volatile release) and the fast path reads State (a volatile acquire) before Value, so a
    // reader that observes CacheClean is guaranteed to see the box of that-or-a-newer clean
    // generation. The read is therefore a linearizable snapshot, not an eventually-consistent one.
    // The causality evidence rides in the same box (issue #39): the stamp AND the per-source map
    // describing which signal versions the value reflects are published in the same atomic swap,
    // so neither can ever be paired with a neighbouring publication's value.
    private volatile ValueBox valueBox = new(default!, StampEvidence.None);

    internal T Value => valueBox.Value;

    // The clean fast-path read as a completed task, cached in the box so it is created at most
    // once per publication: the Get fast paths hand it out directly instead of paying an async
    // state machine whose builder allocates a fresh Task<T> per read (the runtime only caches
    // result tasks for a handful of primitive values).
    internal Task<T> CachedValueTask => valueBox.CompletedTask;

    public override StampEvidence Evidence => valueBox.Evidence;

    // The (value, evidence) pair of one publication -- a single volatile box read, never torn.
    internal (T Value, StampEvidence Evidence) ValueAndEvidence
    {
        get
        {
            var box = valueBox;
            return (box.Value, box.Evidence);
        }
    }

    // The (value, stamp) projection of one publication.
    internal (T Value, CausalityStamp Stamp) ValueAndStamp
    {
        get
        {
            var box = valueBox;
            return (box.Value, box.Evidence.Stamp);
        }
    }

    // The signal write path: publishes the value with its own single-entry stamp (a signal has
    // no sources), in one atomic box swap.
    internal void SetValueAndStamp(T value, CausalityStamp stamp)
    {
        valueBox = new ValueBox(value, StampEvidence.ForOwnStamp(stamp));
    }

    // Publish a computed value together with the evidence captured during the evaluation that
    // produced it. Shared by MemoBase and ConcurrentRace; the race passes the capture it closed
    // at winner selection.
    internal void PublishValueWithCapturedStamps(T value) => PublishValueWithStamps(value, Context.TakeStampCapture(this));

    internal void PublishValueWithStamps(T value, StampCapture capture, int winningBranch = 0)
    {
        valueBox = new ValueBox(value, StampEvidence.FromCapture(capture, winningBranch));
    }

    internal MemoHandlR(Context context) : base(context)
    {
    }

    // The (value, evidence) read every entry point projects from: GetWithStamp keeps only the
    // stamp, while infrastructure fan-ins (ReactionBuilder's isolated-scope dependency
    // evaluations) need the verifiability of the SAME publication as the value -- reading
    // Evidence separately could straddle a concurrent republish. This base implementation is
    // the leaf-signal tracked read; MemoBase and ConcurrentRace override with their pull
    // protocols.
    internal virtual Task<(T Value, StampEvidence Evidence)> ReadWithEvidence() => TrackDependencyAndRead();

    // The leaf-signal tracked read, shared by Signal and EagerRelativeSignal (Get and
    // GetWithStamp alike): when a computation is capturing, register this node as one of its
    // dependencies and record the stamp of the SAME box publication as the returned value -- the
    // pair must never be split, so there is exactly one box read per call. The per-node mutex is
    // deliberately NOT taken -- CheckDependenciesTheSame is already serialized by Context.Lock,
    // and a signal has no recompute for the mutex to guard (ADR 0002). MemoBase overrides
    // ReadWithEvidence with its own cached fast path, so this stays a leaf-signal helper.
    private protected async Task<(T Value, StampEvidence Evidence)> TrackDependencyAndRead()
    {
        ActorFlowGuards.RejectLockNodeReadInsideActorComputation();

        // An unpinned flow can have no capturing reaction (its scope would be freshly minted),
        // so the read needs no scope at all.
        if (!Context.HasFlowScope)
        {
            return ValueAndEvidence;
        }

        var scope = Context.GetOrCreateScope();
        if (scope.CurrentReaction == null)
        {
            return ValueAndEvidence;
        }

        (T Value, StampEvidence Evidence) pair;
        using (await scope.ContextLock.UpgradeableLockAsync())
        {
            // Registration, box read and stamp record fused under one Context.Lock acquisition
            // (see Context.TrackedRead).
            pair = Context.TrackedRead(this, scope);
        }
        GC.KeepAlive(scope); // strong root: the lock identity must outlive the tracked read
        return pair;
    }

    private sealed class ValueBox(T value, StampEvidence evidence)
    {
        public readonly T Value = value;
        public readonly StampEvidence Evidence = evidence;

        // Benign race: two concurrent creations produce interchangeable completed tasks over
        // the same immutable box, so no synchronization is needed.
        private Task<T>? completedTask;
        public Task<T> CompletedTask => completedTask ??= Task.FromResult(Value);
    }
}

/// <summary>
/// One publication's causality evidence (issue #39): the node's own stamp and the per-source
/// map it was sealed from, immutable and published as one reference -- reading this property
/// gives fields that describe the same completed evaluation. The per-source map is wrapped
/// read-only before it becomes reachable, so a consumer can never downcast and mutate the
/// node's published evidence. <see cref="Unverifiable"/> distinguishes "this value depends on
/// no tracked signals" (an honest empty stamp) from "no honest claim can be made about this
/// value" (a mixed/faulted evaluation): both carry the empty stamp, but only the former can be
/// trusted by a consistency check -- and unverifiability is contagious, poisoning the evidence
/// of any evaluation that consumed it.
/// </summary>
public sealed class StampEvidence
{
    private static readonly IReadOnlyDictionary<int, CausalityStamp> NoSourceStamps =
        new System.Collections.ObjectModel.ReadOnlyDictionary<int, CausalityStamp>(new Dictionary<int, CausalityStamp>());

    internal static readonly StampEvidence None = new(CausalityStamp.Empty, NoSourceStamps, false);
    internal static readonly StampEvidence UnverifiableEvidence = new(CausalityStamp.Empty, NoSourceStamps, true);

    public CausalityStamp Stamp { get; }
    public IReadOnlyDictionary<int, CausalityStamp> SourceStamps { get; }
    public bool Unverifiable { get; }

    private StampEvidence(CausalityStamp stamp, IReadOnlyDictionary<int, CausalityStamp> sourceStamps, bool unverifiable)
    {
        Stamp = stamp;
        SourceStamps = sourceStamps;
        Unverifiable = unverifiable;
    }

    // A signal's evidence: its own single-entry stamp, no sources.
    internal static StampEvidence ForOwnStamp(CausalityStamp stamp) => new(stamp, NoSourceStamps, false);

    // A derived node's evidence: the join of the sealed source stamps plus the per-source map
    // (only branch 0 and, for races, the winning branch survive the seal -- see
    // StampCapture.Seal). UNVERIFIABLE evidence -- the empty stamp plus the flag, claiming
    // nothing, which is the only honest description of a value no single stamp can describe --
    // is published when:
    //  - the winning evidence is POISONED (a same-source re-read across different
    //    publications, a consumed source that was itself unverifiable, or a faulted read whose
    //    fallback the evidence would otherwise hide), or
    //  - two DIFFERENT sources disagree on a shared signal (e.g. two memos both depending on s,
    //    read across a Set: one carries {s:0}, the other {s:1} -- joining would over-claim that
    //    the whole value reflects the newer write). The fold detects any such pair: the running
    //    join always carries the maximum trigger seen for a signal, so a conflicting stamp
    //    fails the consistency check no matter the fold order.
    // For a mid-evaluation Set the same write refused the node's Clean commit, so the next
    // recompute publishes clean evidence. An evaluation with NO tracked reads publishes None:
    // the same empty stamp, but verifiable -- the value genuinely depends on nothing.
    internal static StampEvidence FromCapture(StampCapture capture, int winningBranch = 0)
    {
        var (poisoned, stamps) = capture.Seal(winningBranch);
        if (poisoned)
        {
            return UnverifiableEvidence;
        }
        if (stamps.Count == 0)
        {
            return None;
        }

        var joined = CausalityStamp.Empty;
        foreach (var stamp in stamps.Values)
        {
            if (!stamp.IsConsistentWith(joined))
            {
                return UnverifiableEvidence;
            }
            joined = joined.Join(stamp);
        }

        return new(joined, new System.Collections.ObjectModel.ReadOnlyDictionary<int, CausalityStamp>(stamps), false);
    }
}
