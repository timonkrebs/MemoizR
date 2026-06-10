using System.Runtime.CompilerServices;
using xRetry;

namespace MemoizR.Tests;

// Contracts of MemoFactory's context model: keyed factories share one Context (so their nodes
// form one graph), unkeyed factories are isolated, and the static keyed registry holds contexts
// weakly (no leak; CleanUpContexts prunes the dead entries).
public class MemoFactoryTests
{
    [Fact(Timeout = 10000)]
    public async Task KeyedFactories_ShareOneContext_AndTheirNodesInteract()
    {
        var key = $"shared-{Guid.NewGuid():N}";
        var f1 = new MemoFactory(key);
        var f2 = new MemoFactory(key);

        Assert.Same(f1.Context, f2.Context);

        // A cross-factory edge through the shared context behaves like a single graph.
        var v = f1.CreateSignal(1);
        var m = f2.CreateMemoizR(async () => await v.Get() * 2);

        Assert.Equal(2, await m.Get());
        await v.Set(5);
        Assert.Equal(10, await m.Get());
    }

    [Fact]
    public void UnkeyedFactories_GetIsolatedContexts()
    {
        var f1 = new MemoFactory();
        var f2 = new MemoFactory();
        var keyed = new MemoFactory($"iso-{Guid.NewGuid():N}");

        Assert.NotSame(f1.Context, f2.Context);
        Assert.NotSame(f1.Context, keyed.Context);
    }

    // The keyed registry must hold contexts weakly: dropping every factory for a key must make
    // the context collectable (no static leak), and CleanUpContexts prunes without throwing.
    // Depends on the GC actually collecting, so retried like the other GC-dependent tests.
    [RetryFact(3, 200)]
    public void KeyedRegistry_HoldsContextsWeakly_NoLeak()
    {
        var weakContext = CreateAndDropKeyedFactory($"dead-{Guid.NewGuid():N}");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakContext.TryGetTarget(out _),
            "the static keyed registry kept the Context alive after every factory was dropped");

        MemoFactory.CleanUpContexts(); // prunes the dead entry without throwing
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Context> CreateAndDropKeyedFactory(string key)
    {
        var factory = new MemoFactory(key);
        return new WeakReference<Context>(factory.Context);
    }
}
