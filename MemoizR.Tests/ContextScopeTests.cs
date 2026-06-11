using System.Runtime.CompilerServices;
using xRetry;

namespace MemoizR.Tests;

// Contracts of the per-flow scope machinery (Context.GetOrCreateScope / ReactionScope /
// CreateNewScopeIfNeeded / CleanScope / PruneDeadScopes): scope identity within a flow,
// throwaway scopes for unpinned flows, resurrection of a collected scope under a still-pinned
// key, and boundedness of the registry. The whole locking design hangs off scope identity, so
// these pin it directly rather than through graph behavior.
public class ContextScopeTests
{
    [Fact]
    public void GetOrCreateScope_IsStableWithinAFlow_AndAgreesWithTheGetter()
    {
        var ctx = new Context();

        var first = ctx.GetOrCreateScope();   // pins this flow
        var second = ctx.GetOrCreateScope();
        var viaGetter = ctx.ReactionScope;

        Assert.Same(first, second);
        Assert.Same(first, viaGetter);
    }

    [Fact]
    public void UnpinnedFlow_GetsThrowawayScopes_WithoutRegisteringThem()
    {
        var ctx = new Context();

        // No pin on this flow: every getter access hands out a fresh throwaway scope and must
        // NOT grow the registry (registering them was an unbounded leak -- the entries could
        // never be looked up again).
        var s1 = ctx.ReactionScope;
        var s2 = ctx.ReactionScope;

        Assert.NotSame(s1, s2);
        Assert.Equal(0, ctx.RegisteredScopeCount);
    }

    // A pinned flow whose scope got collected (nothing strongly referenced it) must get a fresh
    // scope resurrected under the same key -- and that fresh scope must then be stable. This is
    // the substrate the CleanScope bug class lived on, so it gets a direct pin.
    [RetryFact(3, 200)]
    public void CollectedScope_IsResurrectedFresh_UnderTheSamePinnedKey()
    {
        var ctx = new Context();

        var weakScope = PinAndDropScope(ctx);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakScope.TryGetTarget(out _), "the unreferenced scope should have been collected");

        var resurrected = ctx.ReactionScope;
        Assert.NotNull(resurrected);
        Assert.Same(resurrected, ctx.ReactionScope); // stable again once resurrected
        GC.KeepAlive(resurrected);
    }

    // Synchronous and non-inlined: the AsyncLocal pin must propagate to the caller (a sync
    // callee shares the caller's execution context) while the scope itself stays unreferenced.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ReactionScope> PinAndDropScope(Context ctx)
    {
        return new WeakReference<ReactionScope>(ctx.GetOrCreateScope());
    }

    [Fact]
    public void CleanScope_ThenReuseOnTheSameFlow_ResurrectsCleanly()
    {
        var ctx = new Context();

        Assert.True(ctx.CreateNewScopeIfNeeded());
        var original = ctx.ReactionScope;
        ctx.CleanScope();

        // The flow's key is still pinned but the entry is gone: accesses must mint a fresh,
        // stable scope rather than crash or return null.
        var reused = ctx.ReactionScope;
        Assert.NotNull(reused);
        Assert.NotSame(original, reused);
        Assert.Same(reused, ctx.GetOrCreateScope());
    }

    // The PRODUCTION path version of the boundedness test: no explicit PruneDeadScopes call --
    // the amortized sweep built into scope registration itself must clear the dead entries.
    // The trigger fires once the registrations since the last sweep reach max(64, tableSize/2),
    // so with 200 dead entries it is guaranteed to fire within ~263 further registrations
    // (since >= (200 + since)/2 once since >= 200). Without the sweep the registry would hold
    // ~500 entries at the end; with it, only the post-GC registrations survive.
    [RetryFact(3, 200)]
    public async Task ScopeRegistry_StaysBounded_ViaAmortizedRegistrationSweep()
    {
        var ctx = new Context();

        for (var i = 0; i < 200; i++)
        {
            await Task.Run(() => ctx.GetOrCreateScope());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Drive ONLY the production path: registrations must trigger the sweep on their own.
        for (var i = 0; i < 300; i++)
        {
            await Task.Run(() => ctx.GetOrCreateScope());
        }

        Assert.True(ctx.RegisteredScopeCount <= 350,
            $"scope registry held {ctx.RegisteredScopeCount} entries; the amortized registration sweep never cleared the 200 dead flows");
    }

    // The registry holds scopes weakly, but the ENTRIES are pruned on each new registration:
    // many short-lived flows must not grow it without bound.
    [RetryFact(3, 200)]
    public async Task ScopeRegistry_StaysBounded_AcrossManyDeadFlows()
    {
        var ctx = new Context();

        for (var i = 0; i < 30; i++)
        {
            await Task.Run(() => ctx.GetOrCreateScope());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The next registration sweeps the dead entries.
        await Task.Run(() => ctx.GetOrCreateScope());

        Assert.True(ctx.RegisteredScopeCount <= 3,
            $"scope registry grew to {ctx.RegisteredScopeCount} entries; dead flows are not being pruned");
    }
}
