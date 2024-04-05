namespace MemoizR.Reactive;

public sealed class Reaction<T> : ReactionBase
{
    private readonly IStateGetR<T> memo;
    private readonly Action<T> action;

    internal Reaction(IStateGetR<T> memo,    
                      Action<T> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo = memo;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        action(await memo.Get(cts));
    }
}

public sealed class Reaction<T1, T2> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly Action<T1, T2> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,    
                      Action<T1, T2> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        await Task.WhenAll(task1, task2);
        action(await task1, await task2);
    }
}

public sealed class Reaction<T1, T2, T3> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly Action<T1, T2, T3> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,    
                      Action<T1, T2, T3> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        await Task.WhenAll(task1, task2, task3);
        action(await task1, await task2, await task3);
    }
}

public sealed class Reaction<T1, T2, T3, T4> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly Action<T1, T2, T3, T4> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,    
                      Action<T1, T2, T3, T4> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4);
        action(await task1, await task2, await task3, await task4);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly Action<T1, T2, T3, T4, T5> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,    
                      Action<T1, T2, T3, T4, T5> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5);
        action(await task1, await task2, await task3, await task4, await task5);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly Action<T1, T2, T3, T4, T5, T6> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,    
                      Action<T1, T2, T3, T4, T5, T6> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6);
        action(await task1, await task2, await task3, await task4, await task5, await task6);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,    
                      Action<T1, T2, T3, T4, T5, T6, T7> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly IStateGetR<T9> memo9;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,
                      IStateGetR<T9> memo9,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.memo9 = memo9;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        var task9 = memo9.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8, task9);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8, await task9);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly IStateGetR<T9> memo9;
    private readonly IStateGetR<T10> memo10;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,
                      IStateGetR<T9> memo9,
                      IStateGetR<T10> memo10,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.memo9 = memo9;
        this.memo10 = memo10;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        var task9 = memo9.Get(cts);
        var task10 = memo10.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8, task9, task10);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8, await task9, await task10);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly IStateGetR<T9> memo9;
    private readonly IStateGetR<T10> memo10;
    private readonly IStateGetR<T11> memo11;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,
                      IStateGetR<T9> memo9,
                      IStateGetR<T10> memo10,
                      IStateGetR<T11> memo11,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.memo9 = memo9;
        this.memo10 = memo10;
        this.memo11 = memo11;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        var task9 = memo9.Get(cts);
        var task10 = memo10.Get(cts);
        var task11 = memo11.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8, task9, task10, task11);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8, await task9, await task10, await task11);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly IStateGetR<T9> memo9;
    private readonly IStateGetR<T10> memo10;
    private readonly IStateGetR<T11> memo11;
    private readonly IStateGetR<T12> memo12;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,
                      IStateGetR<T9> memo9,
                      IStateGetR<T10> memo10,
                      IStateGetR<T11> memo11,
                      IStateGetR<T12> memo12,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.memo9 = memo9;
        this.memo10 = memo10;
        this.memo11 = memo11;
        this.memo12 = memo12;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        var task9 = memo9.Get(cts);
        var task10 = memo10.Get(cts);
        var task11 = memo11.Get(cts);
        var task12 = memo12.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8, task9, task10, task11, task12);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8, await task9, await task10, await task11, await task12);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly IStateGetR<T9> memo9;
    private readonly IStateGetR<T10> memo10;
    private readonly IStateGetR<T11> memo11;
    private readonly IStateGetR<T12> memo12;
    private readonly IStateGetR<T13> memo13;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,
                      IStateGetR<T9> memo9,
                      IStateGetR<T10> memo10,
                      IStateGetR<T11> memo11,
                      IStateGetR<T12> memo12,
                      IStateGetR<T13> memo13,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.memo9 = memo9;
        this.memo10 = memo10;
        this.memo11 = memo11;
        this.memo12 = memo12;
        this.memo13 = memo13;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        var task9 = memo9.Get(cts);
        var task10 = memo10.Get(cts);
        var task11 = memo11.Get(cts);
        var task12 = memo12.Get(cts);
        var task13 = memo13.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8, task9, task10, task11, task12, task13);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8, await task9, await task10, await task11, await task12, await task13);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly IStateGetR<T9> memo9;
    private readonly IStateGetR<T10> memo10;
    private readonly IStateGetR<T11> memo11;
    private readonly IStateGetR<T12> memo12;
    private readonly IStateGetR<T13> memo13;
    private readonly IStateGetR<T14> memo14;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,
                      IStateGetR<T9> memo9,
                      IStateGetR<T10> memo10,
                      IStateGetR<T11> memo11,
                      IStateGetR<T12> memo12,
                      IStateGetR<T13> memo13,
                      IStateGetR<T14> memo14,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.memo9 = memo9;
        this.memo10 = memo10;
        this.memo11 = memo11;
        this.memo12 = memo12;
        this.memo13 = memo13;
        this.memo14 = memo14;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        var task9 = memo9.Get(cts);
        var task10 = memo10.Get(cts);
        var task11 = memo11.Get(cts);
        var task12 = memo12.Get(cts);
        var task13 = memo13.Get(cts);
        var task14 = memo14.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8, task9, task10, task11, task12, task13, task14);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8, await task9, await task10, await task11, await task12, await task13, await task14);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly IStateGetR<T9> memo9;
    private readonly IStateGetR<T10> memo10;
    private readonly IStateGetR<T11> memo11;
    private readonly IStateGetR<T12> memo12;
    private readonly IStateGetR<T13> memo13;
    private readonly IStateGetR<T14> memo14;
    private readonly IStateGetR<T15> memo15;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,
                      IStateGetR<T9> memo9,
                      IStateGetR<T10> memo10,
                      IStateGetR<T11> memo11,
                      IStateGetR<T12> memo12,
                      IStateGetR<T13> memo13,
                      IStateGetR<T14> memo14,
                      IStateGetR<T15> memo15,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.memo9 = memo9;
        this.memo10 = memo10;
        this.memo11 = memo11;
        this.memo12 = memo12;
        this.memo13 = memo13;
        this.memo14 = memo14;
        this.memo15 = memo15;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        var task9 = memo9.Get(cts);
        var task10 = memo10.Get(cts);
        var task11 = memo11.Get(cts);
        var task12 = memo12.Get(cts);
        var task13 = memo13.Get(cts);
        var task14 = memo14.Get(cts);
        var task15 = memo15.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8, task9, task10, task11, task12, task13, task14, task15);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8, await task9, await task10, await task11, await task12, await task13, await task14, await task15);
    }
}

public sealed class Reaction<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly IStateGetR<T3> memo3;
    private readonly IStateGetR<T4> memo4;
    private readonly IStateGetR<T5> memo5;
    private readonly IStateGetR<T6> memo6;
    private readonly IStateGetR<T7> memo7;
    private readonly IStateGetR<T8> memo8;
    private readonly IStateGetR<T9> memo9;
    private readonly IStateGetR<T10> memo10;
    private readonly IStateGetR<T11> memo11;
    private readonly IStateGetR<T12> memo12;
    private readonly IStateGetR<T13> memo13;
    private readonly IStateGetR<T14> memo14;
    private readonly IStateGetR<T15> memo15;
    private readonly IStateGetR<T16> memo16;
    private readonly Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> action;

    internal Reaction(IStateGetR<T1> memo1,
                      IStateGetR<T2> memo2,
                      IStateGetR<T3> memo3,
                      IStateGetR<T4> memo4,
                      IStateGetR<T5> memo5,
                      IStateGetR<T6> memo6,
                      IStateGetR<T7> memo7,
                      IStateGetR<T8> memo8,
                      IStateGetR<T9> memo9,
                      IStateGetR<T10> memo10,
                      IStateGetR<T11> memo11,
                      IStateGetR<T12> memo12,
                      IStateGetR<T13> memo13,
                      IStateGetR<T14> memo14,
                      IStateGetR<T15> memo15,
                      IStateGetR<T16> memo16,    
                      Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.memo8 = memo8;
        this.memo9 = memo9;
        this.memo10 = memo10;
        this.memo11 = memo11;
        this.memo12 = memo12;
        this.memo13 = memo13;
        this.memo14 = memo14;
        this.memo15 = memo15;
        this.memo16 = memo16;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        var task6 = memo6.Get(cts);
        var task7 = memo7.Get(cts);
        var task8 = memo8.Get(cts);
        var task9 = memo9.Get(cts);
        var task10 = memo10.Get(cts);
        var task11 = memo11.Get(cts);
        var task12 = memo12.Get(cts);
        var task13 = memo13.Get(cts);
        var task14 = memo14.Get(cts);
        var task15 = memo15.Get(cts);
        var task16 = memo16.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5, task6, task7, task8, task9, task10, task11, task12, task13, task14, task15, task16);
        action(await task1, await task2, await task3, await task4, await task5, await task6, await task7, await task8, await task9, await task10, await task11, await task12, await task13, await task14, await task15, await task16);
    }
}