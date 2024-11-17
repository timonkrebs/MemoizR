namespace MemoizR;

public sealed class Signal<T> : MemoHandlR<T>, IStateGetR<T?>
{
    internal Signal(T value, Context context) : base(context)
    {
        Value = value;
    }

    public async Task Set(T value)
    {
        Console.WriteLine($"enter ExclusiveLockAsync Set {value}");
        // There can be multiple threads updating the CacheState at the same time but no reads should be possible while in the process.
        // Must be Upgradeable because it could change to "Writeble-Lock" if something synchronously reactive is listening.
        using (await Context.ReactionScope.ContextLock.ExclusiveLockAsync())
        {
            Console.WriteLine($"inside ExclusiveLockAsync Set {value}, {Value}");
            if (Equals(Value, value))
            {
                Console.WriteLine($"check Observers {Observers.Length}");
                await Task.WhenAll(Observers.Select(o => o.Stale(CacheState.CacheCheck)));
                return;
            }

            // only updating the value should be locked
            lock (this)
            {
                Value = value;
            }

            Console.WriteLine($"set Observers {Observers.Length}");
            await Task.WhenAll(Observers.Select(o => o.Stale(CacheState.CacheDirty)));
        }
        Console.WriteLine($"exited ExclusiveLockAsync Set {value}");
    }

    public async Task<T?> Get()
    {
        Context.CreateNewScopeIfNeeded();
        if (Context.ReactionScope.CurrentReaction == null)
        {
            return Value;
        }

        // Only one thread should evaluate the graph at a time. otherwise the context could get messed up.
        // This should lead to perf gains because memoization can be utilized more efficiently.
        using (await Context.ReactionScope.ContextLock.UpgradeableLockAsync())
        {
            Context.CheckDependenciesTheSame(this);
        }

        return Value;
    }
}
