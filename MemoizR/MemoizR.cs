namespace MemoizR;

public sealed class MemoizR<T> : MemoBase<T>
{
    private readonly Func<CancellationTokenSource, Task<T>> fn;

    internal MemoizR(Func<CancellationTokenSource, Task<T>> fn, Context context) : base(context)
    {
        this.fn = fn;
    }

    internal override Task<T> ComputeAsync()
    {
        return fn(Context.CancellationTokenSource!);
    }
}
