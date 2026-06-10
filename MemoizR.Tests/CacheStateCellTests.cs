namespace MemoizR.Tests;

// Unit tests for the CacheStateCell protocol -- the correctness core of the cross-flow
// lost-update guard (see docs/architecture/concurrency.md §6 and ADR 0001). Every rule here is
// otherwise pinned only end-to-end through race reproductions; these lock the contract at the
// unit level so a refactor cannot quietly weaken it.
public class CacheStateCellTests
{
    [Fact]
    public void Invalidate_Escalates_AndBumpsGeneration()
    {
        var cell = new CacheStateCell(CacheState.CacheClean);
        var token = cell.Generation;

        Assert.True(cell.Invalidate(CacheState.CacheDirty));
        Assert.Equal(CacheState.CacheDirty, cell.State);

        // The bump must block a commit snapshotted before the invalidation.
        Assert.False(cell.TryCommitClean(token));
        Assert.Equal(CacheState.CacheDirty, cell.State);
    }

    [Fact]
    public void Invalidate_Suppressed_StillBumpsGeneration()
    {
        // The rule that closes the suppressed-Stale lost update: a node already at least this
        // dirty reports "no change" (so the cascade can stop) but the generation must bump so an
        // in-flight evaluation cannot commit Clean over the invalidation it never observed.
        var cell = new CacheStateCell(CacheState.CacheClean);
        cell.Invalidate(CacheState.CacheDirty);

        var token = cell.Generation;
        Assert.False(cell.Invalidate(CacheState.CacheCheck)); // suppressed: Check <= Dirty
        Assert.Equal(CacheState.CacheDirty, cell.State);      // state untouched

        Assert.False(cell.TryCommitClean(token));             // but the commit is refused
        Assert.Equal(CacheState.CacheDirty, cell.State);
    }

    [Fact]
    public void Invalidate_EscalatesOverEvaluating()
    {
        // Evaluating is the LOWEST enum value, so any invalidation escalates over it -- this is
        // what catches a Stale that lands mid-recompute.
        var cell = new CacheStateCell(CacheState.CacheClean);
        var token = cell.BeginEvaluation();
        Assert.Equal(CacheState.Evaluating, cell.State);

        Assert.True(cell.Invalidate(CacheState.CacheCheck)); // 1 > -1: escalates
        Assert.Equal(CacheState.CacheCheck, cell.State);
        Assert.False(cell.TryCommitClean(token));
    }

    [Fact]
    public void InvalidateFromParent_Escalates_WithoutBumpingGeneration()
    {
        // The diamond down-link: a parent marking us dirty during our own same-flow evaluation
        // (we are reading that very parent) must be absorbed -- the commit still succeeds.
        var cell = new CacheStateCell(CacheState.CacheClean);
        var token = cell.BeginEvaluation();

        cell.InvalidateFromParent(CacheState.CacheDirty);
        Assert.Equal(CacheState.CacheDirty, cell.State);     // escalated...

        Assert.True(cell.TryCommitClean(token));             // ...but absorbed: commit wins
        Assert.Equal(CacheState.CacheClean, cell.State);
    }

    [Fact]
    public void InvalidateFromParent_NeverDowngrades()
    {
        var cell = new CacheStateCell(CacheState.CacheClean);
        cell.Invalidate(CacheState.CacheDirty);

        cell.InvalidateFromParent(CacheState.CacheCheck);    // Check < Dirty: ignored
        Assert.Equal(CacheState.CacheDirty, cell.State);
    }

    [Fact]
    public void BeginEvaluation_MarksEvaluating_WithoutBumpingGeneration()
    {
        var cell = new CacheStateCell(CacheState.CacheClean);
        cell.Invalidate(CacheState.CacheDirty);

        var token = cell.BeginEvaluation();
        Assert.Equal(CacheState.Evaluating, cell.State);

        // No invalidation happened since the snapshot: the evaluation may commit.
        Assert.True(cell.TryCommitClean(token));
        Assert.Equal(CacheState.CacheClean, cell.State);
    }

    [Fact]
    public void Generation_SnapshotMatchesBeginEvaluation_WhenUndisturbed()
    {
        // UpdateIfNecessary snapshots via Generation (parent-check phase); Update re-snapshots
        // via BeginEvaluation. With no invalidation in between they must agree, so a commit
        // against the earlier token also succeeds.
        var cell = new CacheStateCell(CacheState.CacheClean);
        cell.Invalidate(CacheState.CacheCheck);

        var outer = cell.Generation;
        var inner = cell.BeginEvaluation();
        Assert.Equal(outer, inner);
    }

    [Fact]
    public void Force_AlwaysSetsState_AndBumpsGeneration()
    {
        // Constructor Dirty and the catch paths: unconditional, and counts as an invalidation
        // against any in-flight evaluation.
        var cell = new CacheStateCell(CacheState.CacheClean);
        var token = cell.BeginEvaluation();

        cell.Force(CacheState.CacheCheck);
        Assert.Equal(CacheState.CacheCheck, cell.State);
        Assert.False(cell.TryCommitClean(token));
        Assert.Equal(CacheState.CacheCheck, cell.State);
    }

    [Fact]
    public void TryCommitClean_IsIdempotent_ForTheSameToken()
    {
        // The double-commit pattern in Update (publish Clean early, re-confirm after the diamond
        // propagation) relies on a second commit against the same undisturbed token succeeding.
        var cell = new CacheStateCell(CacheState.CacheClean);
        cell.Invalidate(CacheState.CacheDirty);
        var token = cell.BeginEvaluation();

        Assert.True(cell.TryCommitClean(token));
        Assert.True(cell.TryCommitClean(token));
        Assert.Equal(CacheState.CacheClean, cell.State);
    }
}
