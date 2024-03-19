namespace MemoizR.Reactive;

public sealed class Reaction<T> : ReactionBase
{
    private readonly IStateGetR<T> memo;
    private readonly Action<T> action;

    internal Reaction(IStateGetR<T> memo, 
    Action<T> action, 
    Context context, 
    SynchronizationContext? synchronizationContext = null, 
    string label = "Label")
    : base(context, synchronizationContext, label)
    {
        this.memo = memo;
        this.action = action;

        Task.Run(Init).GetAwaiter().GetResult();
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        action(await memo.Get(cts));
    }
}


// Class for 2 memos
public sealed class Reaction<T1, T2> : ReactionBase
{
    private readonly IStateGetR<T1> memo1;
    private readonly IStateGetR<T2> memo2;
    private readonly Action<T1, T2> action;

    internal Reaction(IStateGetR<T1> memo1,
    IStateGetR<T2> memo2, 
    Action<T1, T2> action, 
    Context context, 
    SynchronizationContext? synchronizationContext = null, 
    string label = "Label")
    : base(context, synchronizationContext, label)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.action = action;

        Task.Run(Init).GetAwaiter().GetResult();
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        Task[] tasks = [task1,task2];
        await Task.WhenAll(tasks);
        action(task1.Result, task2.Result);
    }
}

// Class for 3 memos
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
                      SynchronizationContext? synchronizationContext = null,
                      string label = "Label")
        : base(context, synchronizationContext, label)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.action = action;

        Task.Run(Init).GetAwaiter().GetResult();
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        await Task.WhenAll(task1, task2, task3);
        action(task1.Result, task2.Result, task3.Result);
    }
}

// Class for 4 memos
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
                      SynchronizationContext? synchronizationContext = null,
                      string label = "Label")
        : base(context, synchronizationContext, label)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.action = action;

        Task.Run(Init).GetAwaiter().GetResult();
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4);
        action(task1.Result, task2.Result, task3.Result, task4.Result);
    }
}

// Classes for 5 to 10 memos follow the same pattern, adding memo fields and corresponding actions

// Class for 5 memos
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
                      SynchronizationContext? synchronizationContext = null,
                      string label = "Label")
        : base(context, synchronizationContext, label)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.action = action;

        Task.Run(Init).GetAwaiter().GetResult();
    }

    protected override async Task Execute(CancellationTokenSource cts)
    {
        var task1 = memo1.Get(cts);
        var task2 = memo2.Get(cts);
        var task3 = memo3.Get(cts);
        var task4 = memo4.Get(cts);
        var task5 = memo5.Get(cts);
        await Task.WhenAll(task1, task2, task3, task4, task5);
        action(task1.Result, task2.Result, task3.Result, task4.Result, task5.Result);
    }
}

// Class for 6 memos
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
                      SynchronizationContext? synchronizationContext = null,
                      string label = "Label")
        : base(context, synchronizationContext, label)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.action = action;

        Task.Run(Init).GetAwaiter().GetResult();
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
        action(task1.Result, task2.Result, task3.Result, task4.Result, task5.Result, task6.Result);
    }
}

// Classes for 7 to 10 memos follow the same pattern, adding memo fields and corresponding actions

// Class for 7 memos
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
                      SynchronizationContext? synchronizationContext = null,
                      string label = "Label")
        : base(context, synchronizationContext, label)
    {
        this.memo1 = memo1;
        this.memo2 = memo2;
        this.memo3 = memo3;
        this.memo4 = memo4;
        this.memo5 = memo5;
        this.memo6 = memo6;
        this.memo7 = memo7;
        this.action = action;

        Task.Run(Init).GetAwaiter().GetResult();
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
        action(task1.Result, task2.Result, task3.Result, task4.Result, task5.Result, task6.Result, task7.Result);
    }
}

// Class for 8 memos
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
                      SynchronizationContext? synchronizationContext = null,
                      string label = "Label")
        : base(context, synchronizationContext, label)
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

        Task.Run(Init).GetAwaiter().GetResult();
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
        action(task1.Result, task2.Result, task3.Result, task4.Result, task5.Result, task6.Result, task7.Result, task8.Result);
    }
}

// Classes for 9 and 10 memos follow the same pattern, adding memo fields and corresponding actions

// Class for 9 memos
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
                      SynchronizationContext? synchronizationContext = null,
                      string label = "Label")
        : base(context, synchronizationContext, label)
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

        Task.Run(Init).GetAwaiter().GetResult();
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
        action(task1.Result, task2.Result, task3.Result, task4.Result, task5.Result, task6.Result, task7.Result, task8.Result, task9.Result);
    }
}

// Class for 10 memos
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
                      SynchronizationContext? synchronizationContext = null,
                      string label = "Label")
        : base(context, synchronizationContext, label)
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

        Task.Run(Init).GetAwaiter().GetResult();
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
        action(task1.Result, task2.Result, task3.Result, task4.Result, task5.Result, task6.Result, task7.Result, task8.Result, task9.Result, task10.Result);
    }
}
