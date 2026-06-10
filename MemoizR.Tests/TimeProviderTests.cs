using Microsoft.Extensions.Time.Testing;

namespace MemoizR.Tests;

// The debounce delay in ReactionBase runs on an injectable TimeProvider (issue #38). With a
// FakeTimeProvider the debounce window elapses only when the test advances the clock, so the
// "nothing ran yet" half of these assertions is airtight instead of racing wall-clock time.
public class TimeProviderTests
{
    // Factory-level registration: every reaction built afterwards debounces on the fake clock.
    // The initial eager run bypasses the debounce (TimeSpan.Zero), so it must complete without
    // the clock ever advancing; a later Set must stay pending until the FULL window elapsed.
    [Fact(Timeout = 10000)]
    public async Task Reaction_FactoryFakeTimeProvider_DebounceElapsesOnlyWhenAdvanced()
    {
        var timeProvider = new FakeTimeProvider();
        var f = new MemoFactory().AddTimeProvider(timeProvider);
        var v1 = f.CreateSignal(1);
        var invocations = 0;
        var last = -1;

        var r = f.BuildReaction()
            .AddDebounceTime(TimeSpan.FromMinutes(5))
            .CreateReaction(v1, v => { Interlocked.Increment(ref invocations); last = v; });

        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref invocations) == 1);
        Assert.Equal(1, last);

        await v1.Set(2);

        // Negative assertions: the fake clock is frozen (then advanced short of the window), so
        // the debounce cannot elapse -- the real-time windows only cover scheduling noise.
        await Task.Delay(100);
        Assert.Equal(1, last);

        timeProvider.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(100);
        Assert.Equal(1, last);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await TestHelpers.WaitForConvergenceAsync(() => last == 2);
        Assert.Equal(2, last);
        Assert.Equal(2, invocations);
        GC.KeepAlive(r);
    }

    // Builder-level registration + the deterministic version of DebounceCoalescesRapidUpdates:
    // every Set lands while the fake clock is frozen, so they are all inside one debounce window
    // by construction, and advancing once must yield exactly one run with the final value.
    [Fact(Timeout = 10000)]
    public async Task Reaction_BuilderFakeTimeProvider_AdvanceCoalescesRapidSetsToSingleRun()
    {
        var timeProvider = new FakeTimeProvider();
        var f = new MemoFactory();
        var v1 = f.CreateSignal(0);
        var invocations = 0;
        var last = -1;

        var r = f.BuildReaction()
            .AddTimeProvider(timeProvider)
            .AddDebounceTime(TimeSpan.FromMinutes(1))
            .CreateReaction(v1, v => { Interlocked.Increment(ref invocations); last = v; });

        await TestHelpers.WaitForConvergenceAsync(() => Volatile.Read(ref invocations) == 1);
        Assert.Equal(0, last);

        for (var i = 1; i <= 10; i++)
        {
            await v1.Set(i);
        }

        await Task.Delay(100);
        Assert.Equal(0, last); // frozen clock: no debounce window may have elapsed

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await TestHelpers.WaitForConvergenceAsync(() => last == 10);
        await Task.Delay(100); // no FURTHER invocation may follow the coalesced one
        Assert.Equal(10, last);
        Assert.Equal(2, invocations);
        GC.KeepAlive(r);
    }

    // AddTimeProvider follows the AddSynchronizationContext capture contract: a builder captures
    // the factory's provider when it is built. A builder created BEFORE the registration keeps
    // the system clock (its debounce elapses in real time, no Advance needed); one created AFTER
    // uses the fake clock (its debounce waits for Advance).
    [Fact(Timeout = 10000)]
    public async Task AddTimeProvider_AppliesToBuildersCreatedAfterRegistration()
    {
        var timeProvider = new FakeTimeProvider();
        var f = new MemoFactory();
        var v1 = f.CreateSignal(1);

        var builderBefore = f.BuildReaction();
        f.AddTimeProvider(timeProvider);

        var lastBefore = -1;
        var rBefore = builderBefore.CreateReaction(v1, v => lastBefore = v);

        var lastAfter = -1;
        var rAfter = f.BuildReaction().CreateReaction(v1, v => lastAfter = v);

        await TestHelpers.WaitForConvergenceAsync(() => lastBefore == 1 && lastAfter == 1);

        await v1.Set(2);

        // rBefore debounces on the system clock (1ms default) and converges on its own ...
        await TestHelpers.WaitForConvergenceAsync(() => lastBefore == 2);
        Assert.Equal(2, lastBefore);
        // ... while rAfter is pinned to the frozen fake clock until the test advances it.
        Assert.Equal(1, lastAfter);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await TestHelpers.WaitForConvergenceAsync(() => lastAfter == 2);
        Assert.Equal(2, lastAfter);
        GC.KeepAlive(rBefore);
        GC.KeepAlive(rAfter);
    }
}
