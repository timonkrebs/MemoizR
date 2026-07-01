namespace MemoizR;

/// <summary>
/// The actor engine's memo (experimental, ADR 0006): dynamic lazy memoization with the same
/// observable semantics as <see cref="MemoizR{T}"/> -- push invalidation, pull recomputation,
/// generation-guarded commits, diamond absorption, dynamic rewiring, a lock-free clean fast
/// path -- but with every piece of bookkeeping running as a <see cref="GraphActor"/> turn
/// instead of under locks. An evaluation is a transaction of turns: Begin (decide + mark
/// Evaluating + snapshot the generation), the user computation off-actor (nested reads recurse
/// freely -- nothing is held, so the lock engine's reentrancy machinery has no counterpart
/// here), then Commit (publish, rewire, diamond-mark, generation-checked Clean).
///
/// At most one evaluation per node runs at a time (the lock engine's per-node mutex) via an
/// actor-confined waiter queue; a reader arriving mid-evaluation parks on a waiter task and
/// re-decides when woken -- UNLESS the read is nested inside this node's own in-flight
/// evaluation (the node appears in the reader's evaluation chain), which is, by definition, a
/// cycle and throws. Unawaited sibling reads of one memo from a single flow are not cycles and
/// wait like any other reader. Waiting holds no lock and no actor, so the lock-ordering
/// analysis of concurrency.md §9 has nothing to analyze here.
/// </summary>
public sealed class ActorMemo<T> : ActorValueNode<T>
{
    private readonly Func<Task<T>> fn;

    // Actor-confined evaluation bookkeeping. `evaluating` (not the state enum) carries the
    // at-most-one-evaluation invariant, because a cascade legitimately overwrites the
    // Evaluating STATE mid-compute (Evaluating is the enum's minimum; any Stale escalates it).
    private bool evaluating;
    private readonly List<TaskCompletionSource> waiters = [];

    internal ActorMemo(Func<Task<T>> fn, Context context)
        : base(default!, context, CacheState.CacheDirty)
    {
        this.fn = fn;
    }

    public Task<T> Get()
    {
        var frame = ActorFlow.CurrentFrame;
        if (frame == null)
        {
            ActorFlowGuards.RejectUntrackedReadInsideLockComputation();
            if (State == CacheState.CacheClean)
            {
                // Lock-free fast path: volatile state (acquire) read before the value box,
                // exactly the ADR 0001 rule-4 pairing -- a reader observing Clean sees the box
                // of that-or-a-newer clean generation.
                return Task.FromResult(Value);
            }

            return Drive(null, null);
        }

        ActorFlowGuards.RejectCrossActorRead(frame, this);

        // A tracked read's frame is also its evaluation chain: the frame of the computation the
        // read is nested inside, linked through its ancestors.
        return Drive(frame, frame);
    }

    // The parent-scan entry point (the analog of IMemoizR.UpdateIfNecessary): bring this node
    // up to date without capturing it as a dependency of the caller. The chain still travels,
    // so a cycle closed through a parent scan is detected.
    internal override async Task EnsureCleanAsync(EvaluationFrame? chain)
    {
        if (State == CacheState.CacheClean)
        {
            return;
        }

        await Drive(null, chain).ConfigureAwait(false);
    }

    // The evaluation driver. Runs on the calling flow; every graph-state access happens inside
    // a turn it awaits. The loop re-decides after every wait, because the world may have
    // changed arbitrarily while parked. The caller's capture frame travels into every
    // value-determining turn, because the (source, generation) pair must be recorded in the
    // same turn the value is served -- recording it any earlier or later would let a
    // concurrent invalidation slip between the value and its evidence. The chain (== frame for
    // tracked reads; capture-free ancestry for scans) travels for cycle detection.
    private async Task<T> Drive(EvaluationFrame? frame, EvaluationFrame? chain)
    {
        while (true)
        {
            var decision = await Actor.Run(() => Decide(frame, chain)).ConfigureAwait(false);
            switch (decision.Kind)
            {
                case DecisionKind.Done:
                    return decision.Value;

                case DecisionKind.Wait:
                    await decision.WaitTask!.ConfigureAwait(false); // waiter tasks never fault
                    continue;

                case DecisionKind.Scan:
                    decision = await ScanAndResolveAsync(decision, frame, chain).ConfigureAwait(false);
                    if (decision.Kind == DecisionKind.Done)
                    {
                        return decision.Value;
                    }

                    goto case DecisionKind.Compute;

                case DecisionKind.Compute:
                    return await ComputeAndCommit(decision.GenSnapshot, frame, chain).ConfigureAwait(false);
            }
        }
    }

    // Turn: classify this Get against the node's current state, atomically with claiming the
    // evaluation when one is needed.
    private Decision Decide(EvaluationFrame? frame, EvaluationFrame? chain)
    {
        if (evaluating)
        {
            if (chain != null && chain.Contains(this))
            {
                // The read is nested inside this node's own in-flight evaluation -- reachable
                // only through fn (or a scan fn triggered): a true dependency cycle. A reader
                // whose chain does NOT include us -- another flow, or an unawaited sibling read
                // of the same computation -- legitimately waits below.
                throw new InvalidOperationException("Cyclic behavior detected");
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add(waiter);
            return Decision.OfWait(waiter.Task);
        }

        switch (State)
        {
            case CacheState.CacheClean:
                frame?.Captured.Add((this, Generation)); // cached value, valid at this generation
                return Decision.OfDone(Value);

            case CacheState.CacheCheck:
                // A parent MAY have changed: verify before recomputing. The scan runs off-actor
                // (parents recompute in parallel with everything else); Sources is safe to hand
                // out because commits swap it wholesale.
                evaluating = true;
                return Decision.OfScan(Generation, Sources);

            default: // CacheDirty -- Evaluating is unreachable here (it implies `evaluating`)
                return BeginCompute();
        }
    }

    private Decision BeginCompute()
    {
        evaluating = true;
        State = CacheState.Evaluating;
        return Decision.OfCompute(Generation);
    }

    // Off-actor: bring every snapshotted source up to date, then resolve the scan in a turn
    // (Done, or Compute when a parent's changed value diamond-marked us Dirty meanwhile).
    private async Task<Decision> ScanAndResolveAsync(Decision scan, EvaluationFrame? frame, EvaluationFrame? chain)
    {
        // Extend the chain with this node (a link-only frame; nothing captures into it): a
        // parent whose recompute reads back into us -- a dependency cycle closed through this
        // scan -- must be detected, not parked on a waiter that this scan itself is blocking.
        var scanChain = new EvaluationFrame(this, chain);

        var parentFaulted = false;
        foreach (var source in scan.Sources!)
        {
            try
            {
                await source.EnsureCleanAsync(scanChain).ConfigureAwait(false);
            }
            catch
            {
                // A parent's recompute threw. Remember it so ResolveScan does NOT commit Clean
                // over an unverified parent: the parent stays Dirty, and a later write to an
                // already-dirty node suppresses its cascade, so it would never re-reach us -- we
                // would serve the last good value forever. Staying CacheCheck makes the next Get
                // re-scan and retry the parent until it recovers (mirrors the lock engine's
                // MemoBase.UpdateIfNecessary parent-faulted handling). If we end up recomputing
                // anyway, our own read of the parent re-surfaces the exception with our context.
                parentFaulted = true;
            }
        }

        return await Actor.Run(() => ResolveScan(scan.GenSnapshot, frame, parentFaulted)).ConfigureAwait(false);
    }

    // Turn: after the parent scan, either a parent's changed value diamond-marked us Dirty
    // (recompute, still inside our claimed evaluation), or nothing changed and we may commit
    // Clean -- IF no invalidation landed during the scan (the generation tells) and no parent
    // faulted (an unverified parent must not be committed over).
    private Decision ResolveScan(int genSnapshot, EvaluationFrame? frame, bool parentFaulted)
    {
        if (State == CacheState.CacheDirty)
        {
            return BeginCompute();
        }

        if (parentFaulted)
        {
            // Could not verify against a faulted parent: leave the node CacheCheck (do not commit
            // Clean) so the next Get re-scans and retries. The caller's dependency pair keeps it
            // wired to us for the eventual resolution, and is recorded BEFORE a bump -- the value
            // served here is unconfirmed, so a caller memo must not be able to commit Clean
            // against it (same rule as the fault paths in ComputeAndCommit/Commit).
            frame?.Captured.Add((this, Generation));
            Generation++;
            EndEvaluation();
            return Decision.OfDone(Value);
        }

        if (Generation == genSnapshot)
        {
            frame?.Captured.Add((this, Generation));
            State = CacheState.CacheClean;
            EndEvaluation();
            return Decision.OfDone(Value);
        }

        // An invalidation landed mid-scan. Observers may have committed Clean against our
        // pre-invalidation value in that window, so re-notify them (the
        // CommitCleanOrRenotifyAsync rule); the node stays non-clean for the next Get. The
        // caller's pair is recorded BEFORE the bump: it consumed a value this node refuses to
        // confirm, so its own commit must see the mismatch and park itself.
        frame?.Captured.Add((this, Generation));
        Generation++;
        PropagateToObservers(CacheState.CacheCheck);
        EndEvaluation();
        return Decision.OfDone(Value);
    }

    private async Task<T> ComputeAndCommit(int genSnapshot, EvaluationFrame? callerFrame, EvaluationFrame? chain)
    {
        // The new frame chains onto the arriving read's ancestry (not the ambient frame: a
        // scan-driven recompute must chain through the scanning node's link, which is never
        // installed as ambient), so fn's nested reads see the full path for cycle detection.
        var frame = new EvaluationFrame(this, chain);
        var previousFrame = ActorFlow.Frame.Value;
        ActorFlow.Frame.Value = frame;

        try
        {
            T newValue;
            try
            {
                newValue = await fn().ConfigureAwait(false);
            }
            catch
            {
                // Deliberate divergence from the lock-based engine (which parks a failed memo at
                // CacheCheck): Dirty guarantees the next Get retries the computation even when
                // the failure happened on the very first run, before any source links existed.
                await Actor.Run(() =>
                {
                    // Record this read in the caller's frame even though it faulted: a caller
                    // that catches our failure and returns a fallback must still wire to us, so
                    // that when we later recover and change, the cascade reaches it. Recorded
                    // BEFORE the bump (like every non-Clean outcome): the fallback consumed a
                    // value this node cannot confirm, so the caller's own commit must see the
                    // generation mismatch and park Dirty -- were the pair recorded post-bump it
                    // would match, the caller would commit Clean over our Dirty, and a later
                    // write to the still-dirty parent would be suppressed, caching the fallback
                    // forever.
                    callerFrame?.Captured.Add((this, Generation));
                    Generation++;
                    State = CacheState.CacheDirty;
                    EndEvaluation();
                }).ConfigureAwait(false);
                throw;
            }
            finally
            {
                ActorFlow.Frame.Value = previousFrame;
            }

            return await Actor.Run(() => Commit(genSnapshot, frame, newValue, callerFrame)).ConfigureAwait(false);
        }
        finally
        {
            // The evaluation is over (committed or faulted): deferred work that captured this
            // frame through ExecutionContext (Task.Run inside fn -- the documented way to
            // schedule a write for after the evaluation) must from now on read as OUTSIDE any
            // evaluation (see ActorFlow.CurrentFrame).
            frame.Expire();
        }
    }

    // Turn: the commit point of the evaluation transaction. The body runs user code (the value
    // comparison's Equals(T)), which can throw; the try/finally is what guarantees the
    // at-most-one-evaluation invariant survives that -- without it, a throwing Equals would
    // leave the node wedged in Evaluating with its waiters never released, hanging every other
    // flow's Get forever.
    private T Commit(int genSnapshot, EvaluationFrame frame, T newValue, EvaluationFrame? callerFrame)
    {
        try
        {
            var oldValue = Value;

            // Compare against the last COMMITTED value BEFORE publishing the new box. Equals is
            // user code: if it throws, the box must still hold the old value so a retry's own
            // comparison stays correct. Publishing first would leave newValue in the box while the
            // catch parks the node Dirty, and the retry would then see new == old, skip the
            // diamond mark, and strand observers that only received CacheCheck.
            var changed = !Equals(oldValue, newValue);

            // Publish the box before the volatile state write below -- the release/acquire pair
            // the lock-free fast path reads against.
            Value = newValue;

            // Rewire to the captured sources: whole-array swap for our up-links, in-place
            // observer edits on the parents (actor-confined, so in-place is safe here).
            foreach (var source in Sources)
            {
                source.RemoveObserver(this);
            }

            Sources = frame.Captured.Select(captured => captured.Source).Distinct().ToArray();
            foreach (var source in Sources)
            {
                source.AddObserver(this);
            }

            // The diamond down-link: only when the value actually changed, and without bumping
            // the observers' generations (same-flow marks are absorbed by their own evaluations).
            if (changed)
            {
                MarkObserversDirtyFromParent();
            }

            // Recorded before any park-bump below: a caller consuming a value this commit cannot
            // confirm must see a generation mismatch at its own commit.
            callerFrame?.Captured.Add((this, Generation));

            // Clean requires BOTH that no invalidation reached this node mid-compute (the
            // generation snapshot) AND that every consumed source is still at the generation it
            // was read at (the late-wiring guard: an invalidation of a source this node was not
            // yet wired to bumps the source's generation but can never cascade here -- the
            // cascade's early termination at already-dirty nodes makes that silence PERMANENT, so
            // it must be detected from the read evidence, not awaited from the push path).
            if (Generation == genSnapshot && SourcesUnchangedSinceRead(frame))
            {
                State = CacheState.CacheClean;
            }
            else
            {
                // Park Dirty (not Check: a stale pair may be a signal's, and signals cannot be
                // re-verified by a scan), bump so OUR consumers' pairs mismatch in turn, and
                // re-notify wired observers that may have raced us to Clean.
                Generation++;
                State = CacheState.CacheDirty;
                PropagateToObservers(CacheState.CacheCheck);
            }

            return newValue;
        }
        catch
        {
            // Any fault in the turn (e.g. a user Equals that throws) must not strand the node:
            // park it Dirty so the next Get retries, re-notify observers that may have committed
            // against the now-published value, and let the fault propagate to this Get's caller.
            // The caller's pair is recorded even though the commit faulted (it can throw before
            // the success-path record above) -- a fallback-returning caller must stay wired to us
            // -- and recorded BEFORE the bump, so that caller cannot commit Clean over a value
            // this node just refused to confirm (see the fn-failure path in ComputeAndCommit).
            callerFrame?.Captured.Add((this, Generation));
            Generation++;
            State = CacheState.CacheDirty;
            PropagateToObservers(CacheState.CacheCheck);
            throw;
        }
        finally
        {
            EndEvaluation();
        }
    }

    private static bool SourcesUnchangedSinceRead(EvaluationFrame frame)
    {
        foreach (var (source, generationAtRead) in frame.Captured)
        {
            if (source.Generation != generationAtRead)
            {
                return false;
            }
        }

        return true;
    }

    private void EndEvaluation()
    {
        evaluating = false;
        if (waiters.Count == 0)
        {
            return;
        }

        // RunContinuationsAsynchronously on the waiters: waking them must not run their
        // re-decide loops inline inside this turn.
        foreach (var waiter in waiters)
        {
            waiter.SetResult();
        }

        waiters.Clear();
    }

    private enum DecisionKind
    {
        Done,
        Wait,
        Scan,
        Compute,
    }

    private readonly struct Decision
    {
        public DecisionKind Kind { get; private init; }
        public T Value { get; private init; }
        public Task? WaitTask { get; private init; }
        public int GenSnapshot { get; private init; }
        public ActorNodeBase[]? Sources { get; private init; }

        public static Decision OfDone(T value) => new() { Kind = DecisionKind.Done, Value = value };
        public static Decision OfWait(Task waitTask) => new() { Kind = DecisionKind.Wait, WaitTask = waitTask, Value = default! };
        public static Decision OfScan(int genSnapshot, ActorNodeBase[] sources) => new() { Kind = DecisionKind.Scan, GenSnapshot = genSnapshot, Sources = sources, Value = default! };
        public static Decision OfCompute(int genSnapshot) => new() { Kind = DecisionKind.Compute, GenSnapshot = genSnapshot, Value = default! };
    }
}
